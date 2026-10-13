using System.Globalization;
using System.Text;

namespace SFVaultBridge.Core;

public sealed record BomNode(string PartNumber, string Description, double Quantity, string UnitQuantity, string BomStructure,
                             string Revision, string Material, string FileName, string FullPath, IReadOnlyList<BomNode> Children,
                             string Vendor = "", string VendorNumber = "", string State = "");

public sealed record BomLine(string Item, BomNode Node, int Depth);

/// <summary>Same ordering the manual Inventor export asks for: Description descending, then renumber.</summary>
public static class BomOrdering
{
    public static IReadOnlyList<BomLine> Flatten(IEnumerable<BomNode> roots)
    {
        var lines = new List<BomLine>();
        Walk(roots, "", 0, lines);
        return lines;
    }

    private static void Walk(IEnumerable<BomNode> nodes, string prefix, int depth, List<BomLine> lines)
    {
        var i = 0;
        foreach (var n in nodes.OrderByDescending(n => n.Description ?? "", StringComparer.OrdinalIgnoreCase)
                               .ThenBy(n => n.PartNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            i++;
            var item = prefix.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : prefix + "." + i.ToString(CultureInfo.InvariantCulture);
            lines.Add(new BomLine(item, n, depth));
            Walk(n.Children, item, depth + 1, lines);
        }
    }
}

/// <summary>The line the purchasing sheet prints to say which model representation the BOM was read from.</summary>
public static class BomSource
{
    public static string Describe(string? modelState, string? designView) =>
        $"Model state: {Or(modelState)} | Design view: {Or(designView)} | BOM view: Structured";

    private static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "(none)" : v.Trim();
}

/// <summary>Tab-delimited BOM in the layout bom_purchasing.read_bom_file reads.</summary>
public static class BomFile
{
    public static readonly string[] Headers = { "Item", "Part Number", "BOM Structure", "Unit QTY", "QTY", "Description", "REV", "State", "Material", "File Name", "Vendor", "Vendor Number" };

    public static void Write(string path, IReadOnlyList<BomLine> lines)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join("\t", Headers)).Append("\r\n");
        foreach (var l in lines)
        {
            var n = l.Node;
            sb.Append(string.Join("\t", new[]
            {
                Field(l.Item), Field(n.PartNumber), Field(n.BomStructure), Field(n.UnitQuantity),
                Field(n.Quantity.ToString("G", CultureInfo.InvariantCulture)), Field(n.Description),
                Field(n.Revision), Field(n.State), Field(n.Material), Field(n.FileName), Field(n.Vendor), Field(n.VendorNumber),
            })).Append("\r\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static string Field(string? value)
    {
        var s = (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
