using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class BomTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-bom-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static BomNode N(string pn, string desc, params BomNode[] kids) =>
        new(pn, desc, 1, "Each", "Normal", "1", "Steel", pn + ".ipt", @"C:\Vault Workspace\DESIGNS\" + pn + ".ipt", kids);

    [Fact]
    public void Sorts_siblings_by_description_descending_and_renumbers()
    {
        var roots = new[] { N("A", "alpha"), N("B", "charlie", N("B2", "a"), N("B1", "z")), N("C", "bravo"), N("D", "") };
        var lines = BomOrdering.Flatten(roots);
        Assert.Equal(new[] { "1", "1.1", "1.2", "2", "3", "4" }, lines.Select(l => l.Item));
        Assert.Equal(new[] { "B", "B1", "B2", "C", "A", "D" }, lines.Select(l => l.Node.PartNumber));
        Assert.Equal(new[] { 0, 1, 1, 0, 0, 0 }, lines.Select(l => l.Depth));
    }

    [Fact]
    public void Ties_break_by_part_number()
    {
        var lines = BomOrdering.Flatten(new[] { N("Z", "same"), N("A", "SAME") });
        Assert.Equal(new[] { "A", "Z" }, lines.Select(l => l.Node.PartNumber));
    }

    [Fact]
    public void Writes_headers_and_escapes_fields()
    {
        var path = Path.Combine(_dir, "bom.txt");
        var node = new BomNode("CD-1", "1/2\" bolt\twith tab", 2.5, "Each", "Purchased", "", "Steel", "CD-1.ipt", @"C:\x\CD-1.ipt", Array.Empty<BomNode>());
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        var text = File.ReadAllText(path);
        Assert.StartsWith("Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tState\tMaterial\tFile Name\tVendor\tVendor Number\r\n", text);
        Assert.Contains("1\tCD-1\tPurchased\tEach\t2.5\t\"1/2\"\" bolt with tab\"\t\t\tSteel\tCD-1.ipt\t\t\r\n", text);
    }

    [Fact]
    public void Writes_vendor_and_vendor_number_last()
    {
        var path = Path.Combine(_dir, "bom-vendor.txt");
        var node = new BomNode("CD-2", "nut", 1, "Each", "Purchased", "", "", "CD-2.ipt", @"C:\x\CD-2.ipt", Array.Empty<BomNode>(), "Acme", "A-9");
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        Assert.Contains("CD-2.ipt\tAcme\tA-9\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Field_flattens_newlines()
    {
        Assert.Equal("a b", BomFile.Field("a\r\nb"));
        Assert.Equal("", BomFile.Field(null));
    }

    [Fact]
    public void Writes_state_after_rev()
    {
        var path = Path.Combine(_dir, "bom-state.txt");
        var node = new BomNode("CD-3", "bolt", 1, "Each", "Purchased", "A", "", "CD-3.ipt", @"C:\x\CD-3.ipt", Array.Empty<BomNode>(), "", "", "Released");
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        Assert.Contains("\tA\tReleased\t", File.ReadAllText(path));
    }
}
