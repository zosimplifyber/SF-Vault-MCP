using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class SourceRulesTests
{
    private static BomNode Node(string structure, string rev = "", string state = "") =>
        new("P", "d", 1, "Each", structure, rev, "", "P.ipt", @"C:\x\P.ipt", Array.Empty<BomNode>(), "", "", state);

    [Theory]
    [InlineData("Part - Purchased", true)]
    [InlineData("Part - Content Center", true)]
    [InlineData("part - purchased", true)]
    [InlineData("Part - Mechanical", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Buy_categories(string? category, bool expected) => Assert.Equal(expected, SourceRules.IsBuyCategory(category));

    [Theory]
    [InlineData("Normal", "Part - Purchased", "Purchased")]
    [InlineData("Normal", "Part - Content Center", "Purchased")]
    [InlineData("Normal", "Part - Mechanical", "Normal")]
    [InlineData("Normal", null, "Normal")]
    [InlineData("Purchased", "Part - Mechanical", "Purchased")]
    [InlineData("Phantom", "Part - Purchased", "Phantom")]
    [InlineData("Reference", "Part - Purchased", "Reference")]
    public void Effective_structure(string structure, string? category, string expected) =>
        Assert.Equal(expected, SourceRules.EffectiveStructure(structure, category));

    [Fact]
    public void Vendor_number_prefers_web_link()
    {
        Assert.Equal("https://x/1", SourceRules.VendorNumber(" https://x/1 ", "A-9"));
        Assert.Equal("A-9", SourceRules.VendorNumber("", "A-9"));
        Assert.Equal("A-9", SourceRules.VendorNumber(null, " A-9 "));
        Assert.Equal("", SourceRules.VendorNumber(null, null));
    }

    [Fact]
    public void Apply_uses_vault_facts_and_falls_back_to_iproperties()
    {
        var a = SourceRules.Apply(Node("Normal", "1"), "Part - Purchased", "Released", "B");
        Assert.Equal(("Purchased", "Released", "B"), (a.BomStructure, a.State, a.Revision));
        var b = SourceRules.Apply(Node("Normal", "1"), null, "", null);
        Assert.Equal(("Normal", "", "1"), (b.BomStructure, b.State, b.Revision));
    }
}
