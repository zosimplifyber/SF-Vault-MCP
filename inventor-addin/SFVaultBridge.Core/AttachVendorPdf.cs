using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Core;

/// <summary>What Vault says about a part for the attach: its Vendor and Vendor Number properties, the names of the files attached to its
/// latest version, and the Vault folder it sits in (the PDF is added beside it).</summary>
public sealed record AttachFacts(string Vendor, string VendorNumber, IReadOnlyList<string> AttachmentNames, string Folder);

/// <summary>A file that is already in Vault (latest version id and master id).</summary>
public sealed record AttachFileRef(long Id, long MasterId, string Name);

/// <summary>The PDF as it stands in Vault after EnsurePdf: its latest version id, and whether this run added it.</summary>
public sealed record PdfInVault(long Id, bool Added);

/// <summary>The Vault side of attach_vendor_pdf. Core decides; the host touches Vault. Everything except Find (inherited) throws
/// DataCardStepException on a failure it can name.</summary>
public interface IAttachPdfHost : IDataCardHost
{
    /// <summary>Vendor, Vendor Number, attachment names and folder of the file's latest version. Reads Vault only (no checkout, no Inventor).</summary>
    AttachFacts ReadFacts(DataCardFileInfo file);

    /// <summary>The latest file with that name in that Vault folder, or null.</summary>
    AttachFileRef? FindInFolder(string folder, string name);

    /// <summary>Returns the existing file when given; otherwise adds localPath to the folder under pdfName with category Documents.
    /// Never adds a second file with a name that is already in the folder.</summary>
    PdfInVault EnsurePdf(string folder, string pdfName, string? localPath, AttachFileRef? existing, string comment);

    /// <summary>Checks the part out, then checks in a new version whose attachment associations are every existing association plus the PDF
    /// (AttachVendorPdf.AttachAssociations). Any failure before the check-in succeeds undoes the checkout.</summary>
    DataCardApplied AttachToPart(DataCardFileInfo file, long pdfFileId, string comment);
}

/// <summary>Gets the vendor's drawing PDF onto this PC (McMaster: get_cad "2-D PDF" through the signed-in McMaster client).</summary>
public interface IVendorPdfFetcher
{
    /// <summary>Downloads the 2-D PDF for the vendor number and returns the local path of a file whose content starts "%PDF".</summary>
    string Fetch(string vendorNumber, string pdfName);

    /// <summary>Deletes a file Fetch returned once it is safely in Vault. Never throws.</summary>
    void Discard(string path);
}

/// <summary>Source is "mcmaster" or the full path of a local PDF (one part only).</summary>
public sealed record AttachPdfRequest(IReadOnlyList<string> Files, string Source, bool DryRun, bool ReviseReleased, string? Comment);

/// <summary>Pure rules for attaching a vendor drawing PDF to an existing part.</summary>
public static class AttachRules
{
    public const string McMasterSource = "mcmaster";
    public const string DocumentsCategory = "Documents";

    public static bool IsMcMasterSource(string source) => string.Equals((source ?? "").Trim(), McMasterSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for "McMASTER-CARR", "McMaster-Carr" and "McMaster Carr" (case, spaces and dashes ignored).</summary>
    public static bool IsMcMasterVendor(string? vendor) =>
        Regex.Replace(vendor ?? "", @"[^A-Za-z]", "").StartsWith("MCMASTER", StringComparison.OrdinalIgnoreCase);

    /// <summary>"CD-001897 McMaster-Carr 91290A115.pdf": the Import New Part rule (ImportRules.PdfFileName). A McMaster part always uses the
    /// folder spelling "McMaster-Carr"; another vendor keeps the part's Vendor text.</summary>
    public static string PdfName(string cd, string? vendor, string vendorNumber)
    {
        var shown = IsMcMasterVendor(vendor) ? McMasterImport.VendorFolder : ImportRules.CleanText(vendor);
        return ImportRules.PdfFileName(cd, new ImportPlan(shown, "", ImportRules.CleanText(vendorNumber), "", "", "", ""));
    }

    /// <summary>Why the part already has its PDF, or null: an attachment with the new name, or any PDF attachment whose name contains the vendor number.</summary>
    public static string? AlreadyAttached(IEnumerable<string> attachmentNames, string pdfName, string vendorNumber)
    {
        var names = (attachmentNames ?? Array.Empty<string>()).ToList();
        var exact = names.FirstOrDefault(n => string.Equals(n, pdfName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return $"{exact} is already attached.";
        var key = ImportRules.NumberKey(vendorNumber);
        if (key.Length == 0) return null;
        var boundary = new Regex(@"(?<![0-9A-Z])" + Regex.Escape(key) + @"(?![0-9A-Z])");
        var hit = names.FirstOrDefault(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && boundary.IsMatch(n.ToUpperInvariant()));
        return hit is null ? null : $"{hit} already has the vendor drawing for {vendorNumber} attached.";
    }

    /// <summary>Why this part is skipped, or null when the PDF should be attached. Checked in order: no Vendor Number, a McMaster download for a
    /// part that is not McMaster's, an attachment that already covers it.</summary>
    public static string? SkipReason(AttachFacts facts, string source, string pdfName)
    {
        var vn = ImportRules.CleanText(facts.VendorNumber);
        if (vn.Length == 0) return "the part has no Vendor Number.";
        if (IsMcMasterSource(source))
        {
            if (!IsMcMasterVendor(facts.Vendor)) return $"not a McMaster part (Vendor is {(string.IsNullOrWhiteSpace(facts.Vendor) ? "blank" : facts.Vendor.Trim())}).";
            if (McMasterImport.NormalizePartNumber(vn) is null) return $"Vendor Number {vn} does not look like a McMaster part number.";
        }
        return AlreadyAttached(facts.AttachmentNames, pdfName, vn);
    }

    /// <summary>Every existing child association of the part (dependencies and attachments, unchanged) plus the PDF as an attachment.
    /// Nothing is dropped or reordered; a PDF that is already an attachment is not added twice.</summary>
    public static IReadOnlyList<AssocRow> AttachAssociations(IReadOnlyList<AssocRow> existing, long parentFileId, long pdfFileId)
    {
        var all = existing.ToList();
        if (!all.Any(r => r.Type == "Attachment" && r.CldFileId == pdfFileId))
            all.Add(new AssocRow(parentFileId, pdfFileId, "Attachment", null, null, null));
        return all;
    }

    /// <summary>True when the first bytes of the file are a PDF header (a UTF-8 BOM and leading white space are allowed).</summary>
    public static bool LooksLikePdf(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1024];
            var n = s.Read(buf, 0, buf.Length);
            return McMasterTools.ContentMatches(buf.AsSpan(0, n).ToArray(), ".pdf");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The attach_vendor_pdf bridge operation: preview or apply, one part at a time, through IAttachPdfHost.</summary>
public static class AttachVendorPdf
{
    public const string Family = "attach_vendor_pdf";

    public static AttachPdfRequest ParseRequest(JsonObject body, PathGuard? guard = null)
    {
        var files = new List<string>();
        if (body["files"] is JsonArray arr)
            files.AddRange(arr.Select((n, i) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s.Trim() : throw new BridgeException("bad_request", $"'files[{i}]' must be a non-empty string.")));
        var one = Args.OptStr(body, "file").Trim();
        if (one.Length > 0) files.Add(one);
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            throw new BridgeException("bad_request", "'file' (or 'files') is required: Vault part names such as CD-001897.ipt or full $/ paths.");
        var source = Args.OptStr(body, "source", AttachRules.McMasterSource).Trim();
        if (source.Length == 0) source = AttachRules.McMasterSource;
        if (AttachRules.IsMcMasterSource(source)) source = AttachRules.McMasterSource;
        else
        {
            if (files.Count != 1) throw new BridgeException("bad_request", "A local PDF source takes exactly one part. Use source \"mcmaster\" for several.");
            if (!source.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("bad_request", "'source' must be \"mcmaster\" or the path of a .pdf file under Downloads.");
            source = guard is null ? Path.GetFullPath(source) : guard.Check(source, "source");
        }
        var comment = Args.OptStr(body, "comment").Trim();
        return new AttachPdfRequest(files, source, Args.Bool(body, "dryRun", true), Args.Bool(body, "reviseReleased", false), comment.Length > 0 ? comment : null);
    }

    public static JsonObject Run(AttachPdfRequest req, IAttachPdfHost host, IVendorPdfFetcher? fetcher, Action<string>? log = null) =>
        Run(req, host, fetcher, log, new DataCardFix.RunState());

    public static JsonObject Run(AttachPdfRequest req, IAttachPdfHost host, IVendorPdfFetcher? fetcher, Action<string>? log, DataCardFix.RunState run)
    {
        var results = new JsonArray();
        var counts = new Dictionary<string, int> { ["preview"] = 0, ["fixed"] = 0, ["no_change"] = 0, ["skipped"] = 0, ["error"] = 0 };
        var files = req.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var arg in files)
        {
            JsonObject r;
            try { r = RunOne(arg, req, host, fetcher, run); }
            catch (BridgeException ex) when (ex.Code == "not_signed_in")
            {
                foreach (var rest in files.SkipWhile(f => !string.Equals(f, arg, StringComparison.OrdinalIgnoreCase)))
                {
                    counts["error"]++;
                    log?.Invoke($"attach_vendor_pdf {rest}: error {ex.Message}");
                    results.Add(DataCardFix.Error(rest, "sign_in", ex.Message));
                }
                break;
            }
            var status = r["status"]!.GetValue<string>();
            counts[status]++;
            var detail = r["reason"] ?? r["message"] ?? r["version"];
            log?.Invoke($"attach_vendor_pdf {r["file"]}: {status}" + (detail is null ? "" : " " + detail));
            results.Add(r);
        }
        return new JsonObject
        {
            ["results"] = results,
            ["counts"] = new JsonObject(counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
    }

    private static string AddedNote(string pdfName) =>
        $" The PDF {pdfName} was added to Vault; run it again and it will be attached, not added twice.";

    private static JsonObject RunOne(string arg, AttachPdfRequest req, IAttachPdfHost host, IVendorPdfFetcher? fetcher, DataCardFix.RunState run)
    {
        DataCardFileInfo? info = null;
        try
        {
            var matches = host.Find(DataCardFix.ParseFile(arg));
            if (matches.Count == 0) return DataCardFix.Skipped(arg, $"{arg} was not found in Vault.");
            if (matches.Count > 1)
                return DataCardFix.Error(arg, "resolve", $"{arg} matches {matches.Count} files ({string.Join(", ", matches.Select(m => m.VaultPath))}). Pass the full $/ path.");
            info = matches[0];
            var skip = DataCardFix.SkipReason(info, req.ReviseReleased);
            if (skip is not null) return DataCardFix.Skipped(info.VaultPath, skip);

            var facts = host.ReadFacts(info);
            var vn = ImportRules.CleanText(facts.VendorNumber);
            var cd = Path.GetFileNameWithoutExtension(info.Name);
            var pdfName = AttachRules.PdfName(cd, facts.Vendor, vn);
            var why = AttachRules.SkipReason(facts, req.Source, pdfName);
            if (why is not null) return DataCardFix.Skipped(info.VaultPath, why);

            var mcmaster = AttachRules.IsMcMasterSource(req.Source);
            if (!mcmaster)
            {
                if (!File.Exists(req.Source)) return DataCardFix.Skipped(info.VaultPath, $"PDF not found: {req.Source}");
                if (!AttachRules.LooksLikePdf(req.Source)) return DataCardFix.Skipped(info.VaultPath, $"{Path.GetFileName(req.Source)} is not a PDF file.");
            }
            var existing = host.FindInFolder(facts.Folder, pdfName);
            var action = existing is not null ? "attach_existing" : mcmaster ? "download_add_attach" : "add_attach";
            var changes = new JsonObject { ["attach_pdf"] = new JsonObject { ["from"] = null, ["to"] = pdfName } };

            var revise = DataCardFix.IsReleased(info);
            RevisePlan? plan = null;
            if (revise && run.ReviseStopped) return DataCardFix.Skipped(info.VaultPath, DataCardFix.ReviseStoppedReason);
            if (revise)
            {
                plan = host.PlanRevise(info);
                if (plan.Problem is not null) return DataCardFix.Skipped(info.VaultPath, plan.Problem);
            }
            var linked = host.LinkedToItem(info);

            if (req.DryRun)
            {
                var preview = new JsonObject
                {
                    ["file"] = info.VaultPath, ["status"] = "preview", ["pdf_name"] = pdfName, ["action"] = action,
                    ["source"] = mcmaster ? "McMaster 2-D PDF" : req.Source, ["vault_folder"] = facts.Folder,
                    ["category"] = AttachRules.DocumentsCategory, ["changes"] = changes,
                };
                if (existing is not null) preview["note"] = $"{pdfName} is already in {facts.Folder} but not attached; it will be attached, not added again.";
                if (revise)
                {
                    preview["would_revise"] = true;
                    preview["transitions"] = new JsonArray(plan!.ToWorkInProgress, plan.ToReleased);
                }
                if (linked == true) preview["linked_to_item"] = true;
                return preview;
            }

            var comment = req.Comment ?? $"Attach vendor PDF {pdfName}";
            string? local = null;
            var downloaded = false;
            if (existing is null)
            {
                if (mcmaster)
                {
                    if (fetcher is null) throw new BridgeException("unavailable", "McMaster download is not available in this build.");
                    try { local = fetcher.Fetch(vn, pdfName); }
                    catch (Exception ex) when (ex is not BridgeException)
                    { throw new DataCardStepException("download", ex.Message); }
                    downloaded = true;
                    if (!AttachRules.LooksLikePdf(local)) throw new DataCardStepException("download", "McMaster sent something that is not a PDF file. Nothing was added to Vault.");
                }
                else local = req.Source;
            }

            PdfInVault pdf;
            try { pdf = host.EnsurePdf(facts.Folder, pdfName, local, existing, comment); }
            catch (DataCardStepException) { throw; }
            catch (Exception ex) when (ex is not BridgeException) { throw new DataCardStepException("pdf", ex.Message); }

            JsonObject result;
            if (revise) result = DataCardFix.ReviseAround(info, changes, comment, host, plan!, fresh => host.AttachToPart(fresh, pdf.Id, comment));
            else
            {
                DataCardApplied applied;
                try { applied = host.AttachToPart(info, pdf.Id, comment); }
                catch (Exception ex) when (pdf.Added && ex is DataCardStepException or DataCardSkipException)
                {
                    throw new DataCardStepException(ex is DataCardStepException se ? se.Step : "skipped", ex.Message + AddedNote(pdfName));
                }
                result = new JsonObject { ["file"] = info.VaultPath, ["status"] = "fixed", ["version"] = applied.Version >= 0 ? applied.Version : null, ["changes"] = changes };
                if (applied.Warning is not null) result["warning"] = applied.Warning;
            }
            result["pdf_name"] = pdfName;
            result["pdf_added"] = pdf.Added;
            if (revise && result["step"]?.GetValue<string>() == "re_release") run.ReviseStopped = true;
            if (linked == true) result["linked_to_item"] = true;
            if (result["status"]?.GetValue<string>() == "fixed")
            {
                if (downloaded && local is not null) fetcher?.Discard(local);
            }
            else if (pdf.Added)
                result["message"] = (result["message"]?.GetValue<string>() ?? "") + AddedNote(pdfName);
            return result;
        }
        catch (DataCardSkipException ex) { return DataCardFix.Skipped(info?.VaultPath ?? arg, ex.Message); }
        catch (DataCardStepException ex) { return DataCardFix.Error(info?.VaultPath ?? arg, ex.Step, ex.Message); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (BridgeException ex) { return DataCardFix.Error(info?.VaultPath ?? arg, ex.Code, ex.Message); }
        catch (Exception ex) { return DataCardFix.Error(info?.VaultPath ?? arg, "unexpected", ex.Message); }
    }
}

/// <summary>Preview then apply for attach_vendor_pdf over MCP. The apply takes the preview_hash and confirm true, once, within 10 minutes.</summary>
public static class AttachPdfTools
{
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] Ignored = { "dryRun" };

    private static JsonObject RequestProps() => Schema.Props(
        ("file", Schema.Str("Vault part to fix: a file name such as CD-001897.ipt or a full $/ path.")),
        ("source", Schema.Str("\"mcmaster\" (default: download the McMaster 2-D PDF) or the full path of a PDF under Downloads.")),
        ("revise_released", Schema.Bool("Allow a released part (state Library, Released...) by moving it to its edit state and back (default false: released parts are skipped).", false)),
        ("comment", Schema.Str("Vault check-in comment. Default names the PDF.")));

    public static IReadOnlyList<McpTool> Build(IAttachPdfHost? host, IVendorPdfFetcher? fetcher, PathGuard downloadsGuard, PreviewStore<AttachPdfRequest> store, Action<string>? log)
    {
        return new[]
        {
            new McpTool("attach_vendor_pdf_preview", "Preview attaching a vendor PDF",
                "Dry run of attaching the vendor's drawing PDF to an existing Vault part that has none (for example an older McMaster part). Writes nothing and downloads nothing. " +
                "Reads the part's Vendor and Vendor Number, skips it with a reason when it already has that PDF attached (by name, or any PDF whose name contains the vendor number), is not a McMaster part, or is a Content Center file, " +
                "and otherwise shows the PDF name (\"<CD> McMaster-Carr <VendorNumber>.pdf\"), whether it would be downloaded and added to the part's folder or an existing unattached file attached, and whether a released part would be revised through its edit state. " +
                "Show the result to the engineer; only after they approve call attach_vendor_pdf_apply with the preview_hash and confirm true within 10 minutes.",
                Schema.Obj(RequestProps(), "file"), McpToolKind.Preview, a => Preview(a, host, fetcher, downloadsGuard, store, log), WriteTimeout),
            new McpTool("attach_vendor_pdf_apply", "Apply attaching a vendor PDF",
                "Downloads the vendor PDF (McMaster sign-in on first use), adds it beside the part in Vault with category Documents, and checks in a new version of the part with the PDF added to its attachments, keeping every existing reference and attachment. " +
                "A released part goes to its edit state and back by lifecycle flags. A WRITE. Needs the preview_hash from attach_vendor_pdf_preview (valid 10 minutes, one use) and confirm true, only after the engineer approved the preview. " +
                "If it returns {status:\"running\", job_id}, the write is still going: call job_status with that job_id and never start it again.",
                Schema.Obj(Schema.Props(
                    ("preview_hash", Schema.Str("The preview_hash returned by attach_vendor_pdf_preview.")),
                    ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes."))), "preview_hash", "confirm"),
                McpToolKind.Write, a => Apply(a, host, fetcher, store, log), WriteTimeout),
        };
    }

    private static JsonObject Canonical(AttachPdfRequest r) => new()
    {
        ["files"] = new JsonArray(r.Files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        ["source"] = r.Source, ["reviseReleased"] = r.ReviseReleased, ["comment"] = r.Comment,
    };

    private static JsonNode Preview(JsonObject a, IAttachPdfHost? host, IVendorPdfFetcher? fetcher, PathGuard guard, PreviewStore<AttachPdfRequest> store, Action<string>? log)
    {
        if (host is null) throw new BridgeException("unavailable", "Attaching a vendor PDF is not available in this build.");
        var body = new JsonObject
        {
            ["file"] = Args.Str(a, "file"), ["source"] = Args.OptStr(a, "source", AttachRules.McMasterSource),
            ["reviseReleased"] = Args.Bool(a, "revise_released", false), ["comment"] = Args.OptStr(a, "comment"), ["dryRun"] = true,
        };
        var req = AttachVendorPdf.ParseRequest(body, guard);
        var result = AttachVendorPdf.Run(req, host, fetcher, log);
        if ((result["counts"]?["preview"]?.GetValue<int>() ?? 0) > 0)
        {
            var hash = PreviewGate.Hash(AttachVendorPdf.Family, Canonical(req), Ignored);
            store.PutAs(hash, req with { DryRun = false });
            result["preview_hash"] = hash;
            result["next"] = $"Show this to the engineer. If they approve, call attach_vendor_pdf_apply with this preview_hash and confirm true within {(int)store.Ttl.TotalMinutes} minutes.";
        }
        else result["next"] = "Nothing to apply: no part would get a PDF.";
        return result;
    }

    private static JsonNode Apply(JsonObject a, IAttachPdfHost? host, IVendorPdfFetcher? fetcher, PreviewStore<AttachPdfRequest> store, Action<string>? log)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Not applied. Show the engineer the attach_vendor_pdf_preview result, and only after they say yes call this again with confirm true.");
        if (host is null) throw new BridgeException("unavailable", "Attaching a vendor PDF is not available in this build.");
        var req = store.Take(Args.Str(a, "preview_hash").Trim())
                  ?? throw new BridgeException("no_matching_preview", $"Not applied. The preview_hash is unknown, already used, or older than {(int)store.Ttl.TotalMinutes} minutes. Run attach_vendor_pdf_preview again, show it to the engineer, then apply.");
        return AttachVendorPdf.Run(req, host, fetcher, log);
    }
}
