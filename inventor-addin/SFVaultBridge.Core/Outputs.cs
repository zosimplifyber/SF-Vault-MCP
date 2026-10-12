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
    public static string ReviewName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Design Review.pdf";
}

public static class ReviewRules
{
    public static bool Skip(BomNode node, string? vaultCategory) =>
        string.Equals(node.BomStructure, "Purchased", StringComparison.OrdinalIgnoreCase)
        || vaultCategory is "Part - Purchased" or "Part - Content Center"
        || node.FullPath.Contains(@"\Content Center Files\", StringComparison.OrdinalIgnoreCase);

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
}

public sealed record ReviewDrawing(string PartNumber, string Drawing, string Pdf);
public sealed record ReviewFailure(string Drawing, string Reason);

public static class ReviewManifest
{
    public static string ToJson(string assembly, string description, string user, string state, string version, DateTime created,
                                IEnumerable<ReviewDrawing> drawings, IEnumerable<string> missing, IEnumerable<ReviewFailure> failed) =>
        new JsonObject
        {
            ["assembly"] = assembly, ["description"] = description, ["user"] = user, ["state"] = state, ["version"] = version,
            ["created"] = created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["drawings"] = new JsonArray(drawings.Select(d => (JsonNode?)new JsonObject { ["part_number"] = d.PartNumber, ["drawing"] = d.Drawing, ["pdf"] = d.Pdf }).ToArray()),
            ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["failed"] = new JsonArray(failed.Select(f => (JsonNode?)new JsonObject { ["drawing"] = f.Drawing, ["reason"] = f.Reason }).ToArray()),
        }.ToJsonString();
}

public sealed record ToolResult(bool Ok, string? Output, int Count, IReadOnlyList<string> Unmatched, string? Error);

public static class ToolResults
{
    private const string NoResult = "SFVaultTools gave no usable result. See bridge.log.";

    public static ToolResult Parse(string? json, string outputKey, string countKey)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResult);
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResult);
        var ok = o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var unmatched = o["unmatched"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : new List<string>();
        var count = o[countKey] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        return new ToolResult(ok, o[outputKey]?.GetValue<string>(), count, unmatched,
                              ok ? null : (o["error"]?.ToString() ?? NoResult));
    }

    public static IReadOnlyList<string> PurchasingArgs(string bom, string assembly, string outDir, string name, string resultPath) =>
        new[] { "purchasing", "--bom", bom, "--assembly", assembly, "--out", outDir, "--name", name, "--result", resultPath };

    public static IReadOnlyList<string> ReviewArgs(string bom, string manifest, string outPdf, string resultPath) =>
        new[] { "review", "--bom", bom, "--manifest", manifest, "--out", outPdf, "--result", resultPath };
}
