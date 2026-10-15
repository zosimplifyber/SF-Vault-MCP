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
    public void Writes_process_after_material()
    {
        var path = Path.Combine(_dir, "bom-process.txt");
        var node = new BomNode("CD-4", "plate", 1, "Each", "Normal", "A", "Aluminum", "CD-4.ipt", @"C:\x\CD-4.ipt", Array.Empty<BomNode>(), Process: " CNC ");
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        Assert.Contains("\tAluminum\tCNC\tCD-4.ipt\t", File.ReadAllText(path));
    }

    [Fact]
    public void Writes_headers_and_escapes_fields()
    {
        var path = Path.Combine(_dir, "bom.txt");
        var node = new BomNode("CD-1", "1/2\" bolt\twith tab", 2.5, "Each", "Purchased", "", "Steel", "CD-1.ipt", @"C:\x\CD-1.ipt", Array.Empty<BomNode>());
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        var text = File.ReadAllText(path);
        Assert.StartsWith("Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tState\tMaterial\tProcess\tFile Name\tVendor\tVendor Number\tCost\tHS/HTS Code\tMcMaster Pack Price\tPack Size\tMcMaster Price Date\tNotes\r\n", text);
        Assert.Contains("1\tCD-1\tPurchased\tEach\t2.5\t\"1/2\"\" bolt with tab\"\t\t\tSteel\t\tCD-1.ipt\t\t\t\t\t\t\t\t\r\n", text);
    }

    [Fact]
    public void Writes_pack_size_without_a_mcmaster_price()
    {
        var path = Path.Combine(_dir, "bom-pack.txt");
        var node = new BomNode("CD-5", "screw", 1, "Each", "Purchased", "", "", "CD-5.ipt", @"C:\x\CD-5.ipt", Array.Empty<BomNode>(), PackSize: 25);
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        var row = File.ReadAllLines(path)[1].Split('\t');
        Assert.Equal(new[] { "", "25", "" }, row.Skip(15).Take(3).ToArray());   // price and date stay blank without a live price
    }

    [Fact]
    public void Writes_vendor_and_vendor_number_last()
    {
        var path = Path.Combine(_dir, "bom-vendor.txt");
        var node = new BomNode("CD-2", "nut", 1, "Each", "Purchased", "", "", "CD-2.ipt", @"C:\x\CD-2.ipt", Array.Empty<BomNode>(), "Acme", "A-9");
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        Assert.Contains("CD-2.ipt\tAcme\tA-9\t\t\t\t\t\t\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Writes_cost_and_hs_code_after_vendor_number()
    {
        var path = Path.Combine(_dir, "bom-cost.txt");
        var withCost = new BomNode("CD-5", "nut", 1, "Each", "Purchased", "", "", "CD-5.ipt", @"C:\x\CD-5.ipt", Array.Empty<BomNode>(), "Acme", "A-9", Cost: 4.5, HsCode: "7318.15");
        var noCost = new BomNode("CD-6", "nut", 1, "Each", "Purchased", "", "", "CD-6.ipt", @"C:\x\CD-6.ipt", Array.Empty<BomNode>());
        BomFile.Write(path, BomOrdering.Flatten(new[] { withCost, noCost }));
        var text = File.ReadAllText(path);
        Assert.Contains("CD-5.ipt\tAcme\tA-9\t4.5\t7318.15\t\t\t\t\r\n", text);
        Assert.Contains("CD-6.ipt\t\t\t\t\t\t\t\t\r\n", text);
    }

    [Fact]
    public void Writes_notes_as_the_last_column_flattened()
    {
        var path = Path.Combine(_dir, "bom-notes.txt");
        var node = new BomNode("CD-7", "insert", 1, "Each", "Normal", "", "", "CD-7.ipt", @"C:\x\CD-7.ipt", Array.Empty<BomNode>(),
                               Notes: "PURCHASE AS NEEDED.\r\nBUY TOOL:\tMcMaster 90240A320");
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        var lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Notes", lines[0].Split('\t')[^1]);
        Assert.Equal(BomFile.Headers.Length, lines[1].Split('\t').Length);
        Assert.EndsWith("\tPURCHASE AS NEEDED. BUY TOOL: McMaster 90240A320", lines[1]);
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
