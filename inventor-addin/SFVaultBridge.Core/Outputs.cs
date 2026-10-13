using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

public static class VaultPaths
{
    public static string ToVaultFolder(string localFile, string workingFolderRoot)
    {
        var root = Path.GetFullPath(workingFolderRoot).TrimEnd('\\', '/') + "\\";
        var full = Path.GetFullPath(localFile);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("not_in_vault", $"{full} is not in the Vault workspace {root.TrimEnd('\\')}. Check the assembly in to Vault first.");
        var dir = Path.GetDirectoryName(full)! + "\\";
        var rel = dir.Substring(root.Length).TrimEnd('\\');
        return rel.Length == 0 ? "$" : "$/" + rel.Replace('\\', '/');
    }

    public static string PurchasingName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Purchasing.xlsx";
    /// <summary>name, or "stem (n).ext" with the first n from 1 that is not taken.</summary>
    public static string UniqueName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    public static string ReviewName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Design Review.pdf";

    public static string AssemblyPackageName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Assembly Package.pdf";
}

public static class ReviewRules
{
    /// <summary>Phantom, Reference and Content Center rows are ignored; Purchased structure or category Part - Purchased is purchased; the rest is made in house.</summary>
    public static PartKind Classify(BomNode node, string? vaultCategory)
    {
        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (Is(node.BomStructure, "Phantom") || Is(node.BomStructure, "Reference")
            || vaultCategory == "Part - Content Center"
            || (node.FullPath ?? "").Contains(@"\Content Center Files\", StringComparison.OrdinalIgnoreCase)) return PartKind.Ignored;
        return Is(node.BomStructure, "Purchased") || vaultCategory == "Part - Purchased" ? PartKind.Purchased : PartKind.Make;
    }

    private static bool IsPdf(string n) => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>First .pdf in the list (an attachment of the purchased part), or null.</summary>
    public static string? FirstPdf(IEnumerable<string> names) => names.FirstOrDefault(IsPdf);

    private static bool IsBoundary(char c) => c is ' ' or '-' or '_' or '.';

    private static bool HasToken(string name, string token, bool atStart)
    {
        for (var i = name.IndexOf(token, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = name.IndexOf(token, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (atStart && i != 0) return false;
            var end = i + token.Length;
            var before = i == 0 || IsBoundary(name[i - 1]);
            var after = end >= name.Length || IsBoundary(name[end]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>
    /// A .pdf named for the part: first those starting with the file stem followed by a boundary (end, space, '-', '_' or '.'),
    /// then those containing the vendor number as a whole token. Ties: shortest name, then alphabetical. Null if none.
    /// </summary>
    public static string? MatchVendorPdf(string fileStem, string vendorNumber, IEnumerable<string> names)
    {
        var pdfs = names.Where(IsPdf).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var stem = fileStem.Trim();
        if (stem.Length > 0)
        {
            var hit = pdfs.FirstOrDefault(n => HasToken(n, stem, atStart: true));
            if (hit is not null) return hit;
        }
        var number = vendorNumber.Trim();
        return number.Length == 0 ? null : pdfs.FirstOrDefault(n => HasToken(n, number, atStart: false));
    }

    public static IReadOnlyList<BomLine> UniqueComponents(IReadOnlyList<BomLine> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return lines.Where(l => seen.Add(l.Node.FullPath)).ToList();
    }

    public static IReadOnlyList<string> PickDrawings(string componentFileName, IEnumerable<string> drawingFileNames)
    {
        static bool IsDrawing(string d) => d.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(componentFileName);
        var all = drawingFileNames.Where(IsDrawing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var same = all.Where(d => Path.GetFileNameWithoutExtension(d).Equals(stem, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(d => d.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        return same.Count > 0 ? new[] { same[0] } : all.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Bookmark(string partNumber, string drawingFileName) => $"{partNumber} - {drawingFileName}";

    /// <summary>Made (not ignored, not purchased) assembly rows only; drawings of these go into the assembly package.</summary>
    public static bool IncludeInAssemblyPackage(BomNode node, string? vaultCategory) =>
        node.FileName.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) && Classify(node, vaultCategory) == PartKind.Make;
}

public enum PartKind { Ignored, Purchased, Make }

public enum PackageMode { Review, Assembly }

public sealed record ReviewPurchased(string PartNumber, string Vendor, string VendorNumber, string Pdf);
public sealed record ReviewDrawing(string PartNumber, string Drawing, string Pdf, string State = "", string Note = "");
public sealed record ReviewFailure(string Drawing, string Reason);

public static class ReviewManifest
{
    private static JsonNode? DrawingJson(ReviewDrawing d)
    {
        var o = new JsonObject { ["part_number"] = d.PartNumber, ["drawing"] = d.Drawing, ["pdf"] = d.Pdf, ["state"] = d.State };
        if (!string.IsNullOrEmpty(d.Note)) o["note"] = d.Note;
        return o;
    }

    public static string ToJson(string assembly, string description, string user, string state, string version, DateTime created,
                                IEnumerable<ReviewDrawing> drawings, IEnumerable<string> missing, IEnumerable<ReviewFailure> failed,
                                IEnumerable<ReviewPurchased> purchased, IEnumerable<string> noVendorDrawing,
                                PackageMode mode = PackageMode.Review, string revision = "") =>
        new JsonObject
        {
            ["assembly"] = assembly, ["description"] = description, ["user"] = user, ["state"] = state, ["version"] = version,
            ["mode"] = mode == PackageMode.Assembly ? "assembly" : "review", ["revision"] = revision,
            ["created"] = created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["drawings"] = new JsonArray(drawings.Select(DrawingJson).ToArray()),
            ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["failed"] = new JsonArray(failed.Select(f => (JsonNode?)new JsonObject { ["drawing"] = f.Drawing, ["reason"] = f.Reason }).ToArray()),
            ["purchased"] = new JsonArray(purchased.Select(p => (JsonNode?)new JsonObject { ["part_number"] = p.PartNumber, ["vendor"] = p.Vendor, ["vendor_number"] = p.VendorNumber, ["pdf"] = p.Pdf }).ToArray()),
            ["no_vendor_drawing"] = new JsonArray(noVendorDrawing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        }.ToJsonString();
}

public sealed record ToolResult(bool Ok, string? Output, int Count, IReadOnlyList<string> Unmatched, string? Error, string? Note = null, int Unreleased = 0, string? Mode = null);

public static class ToolResults
{
    public const string NoResultMessage = "SFVaultTools gave no usable result. See bridge.log.";

    public static ToolResult Parse(string? json, string outputKey, string countKey)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResultMessage);
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResultMessage);
        var ok = o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var unmatched = o["unmatched"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : new List<string>();
        var count = o[countKey] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        return new ToolResult(ok, o[outputKey]?.GetValue<string>(), count, unmatched,
                              ok ? null : (o["error"]?.ToString() ?? NoResultMessage),
                              o["reference_note"] is JsonValue nv && nv.TryGetValue<string>(out var note) && note.Length > 0 ? note : null,
                              o["unreleased"] is JsonValue uv && uv.TryGetValue<int>(out var un) ? un : 0,
                              o["mode"] is JsonValue mv && mv.TryGetValue<string>(out var mode) ? mode : null);
    }

    public static IReadOnlyList<string> PurchasingArgs(string bom, string assembly, string outDir, string name, string resultPath, string? source = null)
    {
        var args = new List<string> { "purchasing", "--bom", bom, "--assembly", assembly, "--out", outDir, "--name", name, "--result", resultPath };
        if (!string.IsNullOrWhiteSpace(source)) { args.Add("--source"); args.Add(source); }
        return args;
    }

    public static IReadOnlyList<string> PackageArgs(string manifest, string outZip, string resultPath) =>
        new[] { "package", "--manifest", manifest, "--out", outZip, "--result", resultPath };

    /// <summary>Every top-level integer field of a tool result; empty when the text is not a JSON object.</summary>
    public static IReadOnlyDictionary<string, int> Counts(string? json)
    {
        var counts = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(json)) return counts;
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return counts; }
        if (o is null) return counts;
        foreach (var kv in o)
            if (kv.Value is JsonValue v && v.TryGetValue<int>(out var n)) counts[kv.Key] = n;
        return counts;
    }

    public static IReadOnlyList<string> ReviewArgs(string bom, string manifest, string outPdf, string resultPath) =>
        new[] { "review", "--bom", bom, "--manifest", manifest, "--out", outPdf, "--result", resultPath };
}
