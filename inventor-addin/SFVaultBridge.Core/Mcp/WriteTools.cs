using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Preview then apply for Fix Data Card. The apply is accepted only with confirm true and the same request as a preview made in the last 10 minutes (matched by hash).</summary>
public static class FixDataCardTools
{
    public const string Family = "fix_data_card";
    private static readonly string[] Ignored = { "confirm", "dryRun" };
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(10);

    private static JsonObject RequestProps() => Schema.Props(
        ("files", Schema.StrList("Vault parts to fix: file names such as CD-001909.ipt or full $/ paths. Only .ipt parts; assemblies are skipped.")),
        ("design", Schema.StrMap("Design Tracking properties to set, by name, for example {\"Vendor\": \"McMASTER-CARR\", \"Description\": \"...\", \"Cost\": \"2.35\"}.")),
        ("custom", Schema.StrMap("Custom (user defined) iProperties to set, for example {\"Source\": \"Buy\", \"Vendor Number\": \"91290A115\", \"Material Finish\": \"Zinc\"}.")),
        ("summary", Schema.StrMap("Summary Information properties to set, for example {\"Title\": \"CD-001909 - Hex Nut\"}.")),
        ("partNumberFromFileName", Schema.Bool("Also set Part Number to the file name without extension (default true).", true)),
        ("reviseReleased", Schema.Bool("Allow fixing a Released file by moving it to its edit state and back (default false: Released files are skipped).", false)),
        ("comment", Schema.Str("Vault check-in comment. Default describes the changed properties.")));

    public static IReadOnlyList<McpTool> Build(IDataCardHost? host, PreviewGate gate, Action<string>? log)
    {
        var previewSchema = Schema.Obj(RequestProps(), "files");
        var applyProps = RequestProps();
        applyProps["confirm"] = Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes.");
        return new[]
        {
            new McpTool("fix_data_card_preview", "Preview data card fix",
                "Dry run of Fix Data Card on existing Vault parts: shows, per file, which iProperties would change (from and to) and which files would be skipped and why. Writes nothing. " +
                "Use it whenever a part's data card (Vendor, Vendor Number, Source, Material, Description, Cost, Title...) is wrong or empty. Show the result to the engineer; " +
                "only after they approve call fix_data_card_apply with the identical request plus confirm true within 10 minutes. The preview binds the request, not Vault's state: apply re-reads each file, so if a file changed in between, what is written can differ. " +
                "Takes a while (each file is downloaded and opened invisibly in Inventor): keep one call to a few files.",
                previewSchema, McpToolKind.Preview, a => Preview(a, host, gate, log), WriteTimeout),
            new McpTool("fix_data_card_apply", "Apply data card fix",
                "Writes the data card changes to Vault: checks each part out, sets the iProperties in Inventor, saves and checks in a new version. A WRITE. " +
                "Requires confirm true and exactly the same request (files, design, custom, summary, options) that fix_data_card_preview returned a preview for in the last 10 minutes; each preview unlocks one apply. " +
                "Never call it without showing the engineer the preview first. If it returns {status:\"running\", job_id}, the write is still going: call job_status with that job_id and never start it again.",
                Schema.Obj(applyProps, "files", "confirm"), McpToolKind.Write, a => Apply(a, host, gate, log), WriteTimeout),
        };
    }

    internal static string HashOf(JsonObject request) => PreviewGate.Hash(Family, request, Ignored);

    private static JsonNode Preview(JsonObject a, IDataCardHost? host, PreviewGate gate, Action<string>? log)
    {
        var req = DataCardFix.ParseRequest(WithDryRun(a, true));
        if (host is null) throw new BridgeException("unavailable", "Fix Data Card is not available in this build.");
        var result = DataCardFix.Run(req, host, log);
        var previews = result["counts"]?["preview"]?.GetValue<int>() ?? 0;
        if (previews > 0)
        {
            gate.Record(HashOf(a));
            result["next"] = $"Show this to the engineer. If they approve, call fix_data_card_apply with this same request plus confirm true within {(int)gate.Ttl.TotalMinutes} minutes.";
        }
        else result["next"] = "Nothing to apply: no file has a pending change.";
        return result;
    }

    private static JsonNode Apply(JsonObject a, IDataCardHost? host, PreviewGate gate, Action<string>? log)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Not applied. Show the engineer the fix_data_card_preview result, and only after they say yes call this again with confirm true.");
        var req = DataCardFix.ParseRequest(WithDryRun(a, false));
        if (host is null) throw new BridgeException("unavailable", "Fix Data Card is not available in this build.");
        if (!gate.Consume(HashOf(a)))
            throw new BridgeException("no_matching_preview", $"Not applied. No preview of exactly this request was made in the last {(int)gate.Ttl.TotalMinutes} minutes (or it was already applied). Run fix_data_card_preview with the same request, show it to the engineer, then apply.");
        return DataCardFix.Run(req, host, log);
    }

    private static JsonObject WithDryRun(JsonObject a, bool dryRun)
    {
        var c = (JsonObject)a.DeepClone();
        c.Remove("confirm");
        c["dryRun"] = dryRun;
        return c;
    }
}

/// <summary>What the McMaster (vendor part) intake needs from Vault and Inventor.</summary>
public interface IIntakeHost
{
    string Designer();
    IReadOnlyList<string> Materials();
    IReadOnlyList<string> Vendors();
    IReadOnlyList<string> Subfolders(string vendor);
    /// <summary>The existing Vault part with the same vendor and vendor number (name and folder path), or null.</summary>
    (string Name, string Folder)? FindDuplicate(string vendor, string vendorNumber);
    /// <summary>The CD number the next import would take. Throws BridgeException("number") when Vault cannot give a usable one.</summary>
    string NextPartNumber();
    IntakeOutcome Run(ImportPlan plan, string stepPath, string pdfPath);
}

public sealed record IntakeOutcome(bool Ok, string? Cd, string? VaultPath, string? FailedStep, string? Error,
                                   IReadOnlyList<string> AlreadyInVault, IReadOnlyList<string> Warnings, string? State);

public sealed record IntakePlanned(ImportPlan Plan, string StepPath, string PdfPath, string StepSha, string PdfSha);

/// <summary>Preview then commit for a vendor part (STEP + PDF in Downloads) using the Import New Part path. The commit takes the preview token and confirm true.</summary>
public static class IntakeTools
{
    public const string DefaultVendor = "McMaster-Carr";
    public static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(10);
    private static readonly System.Text.RegularExpressions.Regex McMasterPn = new(@"^\d{3,5}[A-Z]\d{1,4}$");

    public static IReadOnlyList<McpTool> Build(IIntakeHost? host, PathGuard downloadsGuard, string downloadsDir, PreviewStore<IntakePlanned> store)
    {
        return new[]
        {
            new McpTool("mcmaster_intake_preview", "Preview vendor part import",
                "Plan adding a purchased vendor part (default McMaster-Carr) to Vault as a numbered, categorized Inventor part with its PDF attached, using the same Import New Part path as the ribbon. Writes nothing. " +
                "The engineer first downloads the part's STEP and PDF into their Downloads folder. This finds them (by vendor number in the file name, or use step_path and pdf_path), checks Vault for an existing part with that vendor number " +
                "(then it stops and names it), validates the material against Inventor's library, shows the next CD number, the Vault folder (existing subfolders are listed; prefer one of them) and the iProperties that will be set. " +
                "Show the result to the engineer; only after they approve call mcmaster_intake_commit with the token.",
                Schema.Obj(Schema.Props(
                    ("vendor_number", Schema.Str("The vendor's part number, for example 91290A115 (McMaster part numbers are checked for their usual shape).")),
                    ("description", Schema.Str("The vendor's product name as on existing parts, for example Fully Threaded T-Slot Nut.")),
                    ("material", Schema.Str("Inventor material name for the part; must exist in Inventor's active material library (a wrong name is rejected with suggestions).")),
                    ("vendor", Schema.Str($"Library vendor folder (default {DefaultVendor}).")),
                    ("subfolder", Schema.Str("Subfolder under the vendor folder, for example screws or nuts. Use an existing one when it fits; a new name is created and flagged in the preview. Optional.")),
                    ("material_finish", Schema.Str("Material Finish iProperty, for example Black Oxide. Default As Finished.")),
                    ("comments", Schema.Str("Optional Comments iProperty text (for example the spec lines from the PDF), one per line.")),
                    ("step_path", Schema.Str("Optional full path of the STEP file; must be under Downloads. Default: newest STEP in Downloads whose name contains the vendor number.")),
                    ("pdf_path", Schema.Str("Optional full path of the PDF; must be under Downloads. Default: newest PDF in Downloads whose name contains the vendor number."))),
                    "vendor_number", "description", "material"),
                McpToolKind.Preview, a => Preview(a, host, downloadsGuard, downloadsDir, store)),
            new McpTool("mcmaster_intake_commit", "Commit vendor part import",
                "Adds the previewed vendor part to Vault: takes the next CD number, imports the STEP as a part in Inventor, saves and adds the PDF and the part to Vault with the right categories, then moves the downloads to Downloads\\Imported. A WRITE that takes a minute or more. " +
                "Needs the token from mcmaster_intake_preview (valid 10 minutes, one use) and confirm true, only after the engineer approved the preview. On failure it names the step and lists what already reached Vault; nothing is rolled back. If it returns {status:\"running\", job_id}, the import is still going: call job_status with that job_id and never commit again.",
                Schema.Obj(Schema.Props(
                    ("token", Schema.Str("The token returned by mcmaster_intake_preview.")),
                    ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes."))), "token", "confirm"),
                McpToolKind.Write, a => Commit(a, host, store), CommitTimeout),
        };
    }

    /// <summary>Newest file in the directory (not subfolders) with the extension whose name contains the vendor number on a boundary.</summary>
    public static string? FindDownload(string vendorNumber, string dir, params string[] extensions)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"(?<![0-9A-Z])" + System.Text.RegularExpressions.Regex.Escape(vendorNumber.ToUpperInvariant()) + @"(?![0-9A-Z])");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && pattern.IsMatch(Path.GetFileName(f).ToUpperInvariant()))
            .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f)).FirstOrDefault();
    }

    public static string Sha(string path)
    {
        using var s = System.IO.File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }

    private static JsonNode Preview(JsonObject a, IIntakeHost? host, PathGuard guard, string downloadsDir, PreviewStore<IntakePlanned> store)
    {
        if (host is null) throw new BridgeException("unavailable", "Vendor part import is not available in this build.");
        var vendor = ImportRules.CleanText(Args.OptStr(a, "vendor", DefaultVendor));
        if (vendor.Length == 0) vendor = DefaultVendor;
        var number = ImportRules.CleanText(Args.Str(a, "vendor_number")).ToUpperInvariant().Replace(" ", "");
        if (string.Equals(vendor, DefaultVendor, StringComparison.OrdinalIgnoreCase) && !McMasterPn.IsMatch(number))
            throw new BridgeException("bad_request", $"'{number}' does not look like a McMaster part number (for example 90510A313). Check it, or pass another vendor.");
        var sub = Args.OptStr(a, "subfolder").Trim().Trim('/');
        var plan = new ImportPlan(vendor, sub, number, ImportRules.CleanText(Args.Str(a, "description")), Args.Str(a, "material").Trim(),
            ImportRules.NormalizeFinish(Args.OptStr(a, "material_finish")), host.Designer(), ImportRules.CleanComments(Args.OptStr(a, "comments")));
        var problems = ImportRules.Validate(plan).ToList();
        if (problems.Count > 0) throw new BridgeException("bad_request", string.Join(" ", problems));

        var materials = host.Materials();
        if (materials.Count > 0 && !materials.Any(m => string.Equals(m, plan.Material, StringComparison.OrdinalIgnoreCase)))
        {
            var near = materials.Where(m => m.Contains(plan.Material, StringComparison.OrdinalIgnoreCase) || plan.Material.Contains(m, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
            if (near.Count == 0) near = materials.Take(15).ToList();
            throw new BridgeException("material", $"Material '{plan.Material}' is not in Inventor's active material library. Choose one of: {string.Join(", ", near)}.");
        }

        var dup = host.FindDuplicate(vendor, number);
        if (dup is { } d)
            return new JsonObject
            {
                ["ok"] = false, ["error"] = "duplicate", ["existing_part"] = Path.GetFileNameWithoutExtension(d.Name), ["existing_folder"] = d.Folder,
                ["message"] = $"Already in Vault as {Path.GetFileNameWithoutExtension(d.Name)} ({d.Folder}). Nothing to import; use that part.",
            };

        var step = Pick(a, "step_path", guard, () => FindDownload(number, downloadsDir, ".step", ".stp"), "STEP", number, downloadsDir);
        var pdf = Pick(a, "pdf_path", guard, () => FindDownload(number, downloadsDir, ".pdf"), "PDF", number, downloadsDir);
        var existingSubs = vendor.Length > 0 ? host.Subfolders(vendor) : Array.Empty<string>();
        var newFolder = sub.Length > 0 && !existingSubs.Any(s => string.Equals(s, sub, StringComparison.OrdinalIgnoreCase));
        var vendorKnown = host.Vendors().Any(v => string.Equals(v, vendor, StringComparison.OrdinalIgnoreCase));
        var cd = host.NextPartNumber();

        var planned = new IntakePlanned(plan, step, pdf, Sha(step), Sha(pdf));
        var token = store.Put(planned);
        var vaultFolder = ImportRules.VaultFolder(plan);
        var o = new JsonObject
        {
            ["preview"] = true, ["token"] = token, ["expires_in_minutes"] = (int)store.Ttl.TotalMinutes,
            ["next_cd_number"] = cd + " (indicative; Vault assigns the number at commit)",
            ["vault_folder"] = vaultFolder,
            ["files"] = new JsonObject { ["step"] = step, ["pdf"] = pdf, ["part_file"] = ImportRules.PartFileName(cd), ["pdf_in_vault"] = ImportRules.PdfFileName(cd, plan) },
            ["categories"] = new JsonObject { ["part"] = "Part - Purchased", ["pdf"] = "Documents" },
            ["iproperties"] = new JsonObject
            {
                ["Vendor"] = ImportRules.DataCardVendor(vendor), ["Vendor Number"] = number, ["Catalog Web Link"] = number, ["Description"] = plan.Description,
                ["Title"] = ImportRules.PartTitle(cd, plan.Description), ["Material"] = plan.Material, ["Material Finish"] = plan.MaterialFinish,
                ["Source"] = "Buy", ["Designer"] = plan.Designer, ["Engr Approved By"] = "NOT REVIEWED",
            },
            ["new_vendor_folder"] = !vendorKnown,
            ["new_subfolder"] = newFolder,
            ["existing_subfolders"] = new JsonArray(existingSubs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["next"] = "Show this to the engineer. Only on their yes call mcmaster_intake_commit with this token and confirm true.",
        };
        if (plan.Comments.Length > 0) o["comments"] = plan.Comments;
        if (newFolder) o["note"] = $"Subfolder '{sub}' does not exist under {vendor} and will be created. Pick an existing one if it fits.";
        return o;
    }

    private static string Pick(JsonObject a, string key, PathGuard guard, Func<string?> find, string kind, string number, string dir)
    {
        var given = Args.OptStr(a, key).Trim();
        string path;
        if (given.Length > 0) path = guard.Check(given, key);
        else path = find() ?? throw new BridgeException("not_found", $"No {kind} for {number} in {dir}. Ask the engineer to download it first (the file name must contain {number}), or pass {key}.");
        if (!System.IO.File.Exists(path)) throw new BridgeException("not_found", $"{kind} file not found: {path}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var ok = kind == "PDF" ? ext == ".pdf" : ext is ".step" or ".stp";
        if (!ok) throw new BridgeException("bad_request", $"{key} must be a {kind} file: {path}");
        return path;
    }

    private static JsonNode Commit(JsonObject a, IIntakeHost? host, PreviewStore<IntakePlanned> store)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Not imported. Show the engineer the mcmaster_intake_preview result, and only after they say yes call this again with confirm true.");
        if (host is null) throw new BridgeException("unavailable", "Vendor part import is not available in this build.");
        var p = store.Take(Args.Str(a, "token"))
                ?? throw new BridgeException("no_matching_preview", $"Preview token is unknown, already used, or older than {(int)store.Ttl.TotalMinutes} minutes. Run mcmaster_intake_preview again.");
        foreach (var (path, sha) in new[] { (p.StepPath, p.StepSha), (p.PdfPath, p.PdfSha) })
            if (!System.IO.File.Exists(path) || Sha(path) != sha)
                throw new BridgeException("changed_since_preview", $"{Path.GetFileName(path)} changed or disappeared since the preview. Run mcmaster_intake_preview again.");
        var r = host.Run(p.Plan, p.StepPath, p.PdfPath);
        var o = new JsonObject { ["ok"] = r.Ok };
        if (r.Ok)
        {
            o["part"] = r.Cd; o["vault_path"] = r.VaultPath;
            if (!string.IsNullOrEmpty(r.State)) o["state"] = r.State;
        }
        else
        {
            o["failed_step"] = r.FailedStep; o["error"] = r.Error;
            if (r.Cd is not null) o["cd_number"] = r.Cd;
        }
        o["already_in_vault"] = new JsonArray(r.AlreadyInVault.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        o["warnings"] = new JsonArray(r.Warnings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return o;
    }
}

/// <summary>Builds the full tool list and exposes the read tools as bridge HTTP ops.</summary>
public static class McpCatalog
{
    public static IReadOnlyList<McpTool> Build(IVaultReader vault, IInventorReader inventor, IVaultSession session, IStepImporter importer,
                                               string bridgeVersion, IDataCardHost? dataCard, IIntakeHost? intake, PathGuard downloadsGuard,
                                               string downloadsDir, JobManager jobs, Action<string>? log = null)
    {
        var list = new List<McpTool>();
        var reads = ReadTools.Build(vault, inventor, session, importer, bridgeVersion, () => list.Count);
        list.AddRange(reads);
        list.AddRange(FixDataCardTools.Build(dataCard, new PreviewGate(), log));
        list.AddRange(IntakeTools.Build(intake, downloadsGuard, downloadsDir, new PreviewStore<IntakePlanned>()));
        list.Add(JobTools.Build(jobs));
        return list;
    }

    /// <summary>Every read tool is also POST /op/&lt;name&gt; with the same arguments.</summary>
    public static void RegisterReadOps(Router router, IEnumerable<McpTool> tools, Func<Func<JsonNode?>, JsonNode?> onUi, TimeSpan? timeout = null)
    {
        foreach (var t in tools.Where(t => t.Kind == McpToolKind.Read))
        {
            var tool = t;
            var limit = timeout ?? tool.Timeout ?? McpServer.DefaultCallTimeout;
            router.Register(tool.Name, body => tool.UiThread ? UiCall.Run(onUi, tool.Name, () => tool.Handler(body), limit) : tool.Handler(body));
        }
    }
}
