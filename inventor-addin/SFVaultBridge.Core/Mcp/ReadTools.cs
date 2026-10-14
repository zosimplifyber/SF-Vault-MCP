using System.Globalization;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>The read-only MCP tools (also bridge HTTP ops of the same name): contract in, JSON out. No Vault or Inventor types here.</summary>
public static class ReadTools
{
    public const int SearchDefault = 25, SearchMax = 100;
    public const int LevelsMax = 5, RelationRowsMax = 300;
    public const int VersionsDefault = 20, VersionsMax = 100;
    public const int BomRowsDefault = 1000, BomRowsMax = 3000;

    private static JsonObject FileProps() => Schema.Props(
        ("file", Schema.Str("A Vault file: a file name such as CD-001909.ipt (the .ipt/.iam extension may be left off), or a full path starting with $/. A name that matches several files is rejected with their paths.")),
        ("file_id", Schema.Int("Alternative to 'file': the numeric file_id from vault_search_files or vault_file_versions.", min: 1)));

    private static (string, JsonObject) FileProp() => ("file", (JsonObject)FileProps()["file"]!.DeepClone());
    private static (string, JsonObject) FileIdProp() => ("file_id", (JsonObject)FileProps()["file_id"]!.DeepClone());

    public static IReadOnlyList<McpTool> Build(IVaultReader vault, IInventorReader inventor, IVaultSession session, IStepImporter importer, string bridgeVersion,
                                                Func<int> toolCount) => new[]
    {
        new McpTool("bridge_status", "Bridge status",
            "Check that the SF Vault Bridge works: whether Vault is signed in (user, vault, server), the Inventor version, the active Inventor document and the add-in version. " +
            "Call this first when another tool says not_signed_in or when unsure the engineer's Inventor session is ready.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => Status(session, inventor, importer, bridgeVersion, toolCount())),

        new McpTool("vault_search_files", "Search Vault files",
            "Find files in the engineer's Vault by text in the file name or in Description, Title, Part Number or Vendor Number (contains match, latest version of each file). " +
            "Use it to locate a part, assembly or drawing by CD number, vendor number or words. Returns name, folder, state, revision, category, version and file_id; pass file_id or the path to the other vault_* tools. Read-only.",
            Schema.Obj(Schema.Props(
                ("query", Schema.Str("Text to look for, for example CD-001909 or 91290A115 or hex nut.")),
                ("limit", Schema.Int($"Most files to return (default {SearchDefault}, max {SearchMax}).", 1, SearchMax, SearchDefault))), "query"),
            McpToolKind.Read, a => SearchFiles(vault, a)),

        new McpTool("vault_get_file", "Get Vault file",
            "Everything Vault knows about one file: category, lifecycle state (and whether that state is released), revision, version, size, dates, check-out, " +
            "every filled-in data card property (Vendor, Vendor Number, Material, Source, Cost and so on), Property Compliance and whether it is linked to an item. Read-only.",
            Schema.Obj(FileProps()), McpToolKind.Read, a => GetFile(vault, a)),

        new McpTool("vault_where_used", "Where a file is used",
            "List the parents of a file: the assemblies and drawings that reference it, and parts it is attached to. levels=1 gives direct parents; more levels climb up the assembly tree (max 5). " +
            "Use it before changing or releasing a part to see what it affects. Read-only.",
            Schema.Obj(Schema.Props(FileProp(), FileIdProp(),
                ("levels", Schema.Int($"How many parent levels to climb (default 1, max {LevelsMax}).", 1, LevelsMax, 1)))),
            McpToolKind.Read, a => Relations(vault, a, usedBy: true)),

        new McpTool("vault_uses", "Files a file uses",
            "List the children of an assembly or part: the files it references (and attachments such as vendor PDFs), with the version each parent references and whether that is the latest version. " +
            "levels=1 gives direct children; more levels walk down the assembly tree (max 5). Read-only.",
            Schema.Obj(Schema.Props(FileProp(), FileIdProp(),
                ("levels", Schema.Int($"How many child levels to walk (default 1, max {LevelsMax}).", 1, LevelsMax, 1)))),
            McpToolKind.Read, a => Relations(vault, a, usedBy: false)),

        new McpTool("vault_file_versions", "Vault file versions",
            "Version history of a file, newest first: version number, revision label, lifecycle state, check-in comment, date and user. Read-only.",
            Schema.Obj(Schema.Props(FileProp(), FileIdProp(),
                ("limit", Schema.Int($"Most versions to return (default {VersionsDefault}, max {VersionsMax}).", 1, VersionsMax, VersionsDefault)))),
            McpToolKind.Read, a => Versions(vault, a)),

        new McpTool("inventor_open_bom", "Open assembly BOM",
            "The bill of materials of the assembly that is open and active in Inventor right now (unsaved edits included), all levels, with Vault state and revision applied. " +
            "Each row has item number, part number, description, quantity, BOM structure (Purchased where the Vault category is a buy category), Source, Process, Material, finish, vendor, vendor number, cost, state and revision. " +
            "Fails with no_assembly if the active document is not an assembly. Read-only; it never saves or changes the assembly.",
            Schema.Obj(Schema.Props(
                ("max_depth", Schema.Int("Only rows down to this level (1 = top level only). Default all levels.", 1, 20)),
                ("max_rows", Schema.Int($"Most rows to return (default {BomRowsDefault}, max {BomRowsMax}).", 1, BomRowsMax, BomRowsDefault)))),
            McpToolKind.Read, a => OpenBom(inventor, a)),
    };

    // ---- handlers ----

    internal static JsonNode Status(IVaultSession session, IInventorReader inventor, IStepImporter importer, string version, int tools)
    {
        var s = session.Status();
        string? doc = null;
        try { doc = inventor.ActiveDocument(); } catch (Exception) { }
        var o = new JsonObject
        {
            ["signed_in"] = s.SignedIn, ["user"] = s.User, ["vault"] = s.Vault, ["server"] = s.Server,
            ["inventor_version"] = importer.InventorVersion, ["bridge_version"] = version, ["active_document"] = doc, ["tool_count"] = tools,
        };
        if (!s.SignedIn) o["hint"] = BridgeOps.NotSignedInMessage;
        return o;
    }

    internal static JsonNode SearchFiles(IVaultReader vault, JsonObject a)
    {
        var query = Args.Str(a, "query").Trim();
        if (query.Length < 2) throw new BridgeException("bad_request", "'query' needs at least 2 characters.");
        var limit = Clamp(a, "limit", SearchDefault, 1, SearchMax);
        var r = vault.SearchFiles(query, limit);
        var files = r.Files.Take(limit).ToList();
        var o = new JsonObject { ["query"] = query, ["count"] = files.Count, ["files"] = Arr(files.Select(File)) };
        if (r.Truncated || r.Files.Count > limit) o["truncated"] = true;
        if (files.Count == 0) o["hint"] = "No match. Try a shorter text, the vendor number, or part of the description.";
        return o;
    }

    internal static JsonNode GetFile(IVaultReader vault, JsonObject a)
    {
        var d = vault.GetFile(FileRefRules.Parse(a));
        var o = File(d.File);
        if (d.Released is { } rel) o["released"] = rel;
        o["size_bytes"] = d.SizeBytes;
        Put(o, "modified", d.Modified); Put(o, "created", d.Created); Put(o, "created_by", d.CreatedBy); Put(o, "comment", d.Comment);
        if (d.Compliance is not null) o["property_compliance"] = d.Compliance;
        if (d.LinkedToItem is { } l) o["linked_to_item"] = l;
        o["properties"] = new JsonObject(d.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value)));
        return o;
    }

    internal static JsonNode Relations(IVaultReader vault, JsonObject a, bool usedBy)
    {
        var levels = Clamp(a, "levels", 1, 1, LevelsMax);
        var r = usedBy ? vault.WhereUsed(FileRefRules.Parse(a), levels, RelationRowsMax) : vault.Uses(FileRefRules.Parse(a), levels, RelationRowsMax);
        var rows = r.Rows.Take(RelationRowsMax).Select(x =>
        {
            var o = File(x.File);
            o["relation"] = x.Type;
            o["level"] = x.Level;
            Put(o, usedBy ? "uses" : "used_by", x.Via);
            if (!usedBy && x.ReferencedVersion > 0)
            {
                o["referenced_version"] = x.ReferencedVersion;
                o["referenced_is_latest"] = x.ReferencedVersion >= x.File.LatestVersion;
            }
            return o;
        }).ToList();
        var res = new JsonObject
        {
            ["file"] = File(r.Subject), ["levels"] = levels, ["count"] = rows.Count,
            [usedBy ? "used_by" : "uses"] = Arr(rows),
        };
        if (r.Truncated || r.Rows.Count > RelationRowsMax) res["truncated"] = true;
        return res;
    }

    internal static JsonNode Versions(IVaultReader vault, JsonObject a)
    {
        var limit = Clamp(a, "limit", VersionsDefault, 1, VersionsMax);
        var r = vault.Versions(FileRefRules.Parse(a), limit);
        var rows = r.Versions.OrderByDescending(v => v.Version).Take(limit).Select(v =>
        {
            var o = new JsonObject { ["version"] = v.Version, ["file_id"] = v.FileId };
            Put(o, "revision", v.Revision); Put(o, "state", v.State); Put(o, "comment", v.Comment);
            Put(o, "modified", v.Modified); Put(o, "checked_in_by", v.CheckedInBy);
            return o;
        }).ToList();
        var res = new JsonObject { ["file"] = File(r.Subject), ["count"] = rows.Count, ["versions"] = Arr(rows) };
        if (r.Truncated || r.Versions.Count > limit) res["truncated"] = true;
        return res;
    }

    internal static JsonNode OpenBom(IInventorReader inventor, JsonObject a)
    {
        var maxDepth = Clamp(a, "max_depth", 20, 1, 20);
        var maxRows = Clamp(a, "max_rows", BomRowsDefault, 1, BomRowsMax);
        var bom = inventor.OpenBom();
        var lines = BomOrdering.Flatten(bom.Roots).Where(l => l.Depth + 1 <= maxDepth).ToList();
        var rows = lines.Take(maxRows).Select(l =>
        {
            var n = l.Node;
            var o = new JsonObject { ["item"] = l.Item, ["part_number"] = n.PartNumber };
            Put(o, "description", n.Description);
            o["qty"] = n.Quantity;
            Put(o, "structure", n.BomStructure); Put(o, "source", n.Source); Put(o, "process", n.Process);
            Put(o, "material", n.Material); Put(o, "finish", n.MaterialFinish);
            Put(o, "vendor", n.Vendor); Put(o, "vendor_number", n.VendorNumber);
            if (n.Cost is { } c) o["cost"] = c;
            Put(o, "hs_code", n.HsCode); Put(o, "state", n.State); Put(o, "revision", n.Revision); Put(o, "file", n.FileName);
            if (n.IsSheetMetal) o["sheet_metal"] = true;
            return o;
        }).ToList();
        var res = new JsonObject
        {
            ["assembly"] = bom.Assembly, ["file"] = bom.FileName, ["bom_source"] = bom.SourceLine, ["count"] = rows.Count, ["rows"] = Arr(rows),
        };
        if (lines.Count > rows.Count) { res["truncated"] = true; res["total_rows"] = lines.Count; }
        return res;
    }

    // ---- shaping ----

    internal static JsonObject File(VaultFileSummary f)
    {
        var o = new JsonObject { ["name"] = f.Name, ["path"] = f.Path };
        Put(o, "state", f.State); Put(o, "revision", f.Revision); Put(o, "category", f.Category);
        o["version"] = f.Version;
        if (f.LatestVersion > f.Version) o["latest_version"] = f.LatestVersion;
        o["file_id"] = f.FileId;
        if (f.CheckedOut) o["checked_out_by"] = f.CheckedOutBy.Length > 0 ? f.CheckedOutBy : "someone";
        return o;
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) o[key] = value;
    }

    private static JsonArray Arr(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    /// <summary>An optional integer argument kept inside min..max; a wrong type is bad_request, an out-of-range number is clamped.</summary>
    internal static int Clamp(JsonObject a, string key, int fallback, int min, int max)
    {
        if (a[key] is null) return fallback;
        var v = a[key] is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : throw new BridgeException("bad_request", $"'{key}' must be an integer.");
        return (int)Math.Clamp(v, min, max);
    }

    public static string FormatDate(DateTime d) => d == default ? "" : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
