using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class McMasterRuleTests
{
    private static readonly PrintMaterials Mats = new(BridgeSettings.Defaults.PrintMaterials);

    private static BomNode N(string file, string vendor = "", string folder = @"C:\Vault Workspace\DESIGNS\", string material = "Steel",
                             string structure = "Normal", string process = "", string vendorNumber = "", string state = "Released", params BomNode[] kids) =>
        new(Path.GetFileNameWithoutExtension(file), "d " + file, 2, "Each", structure, "A", material, file, folder + file, kids,
            Vendor: vendor, VendorNumber: vendorNumber, State: state, Process: process);

    [Theory]
    [InlineData("McMaster-Carr")]
    [InlineData("McMASTER-CARR")]
    [InlineData("McMaster Carr")]
    [InlineData("mcmaster-carr")]
    [InlineData("  McMaster-Carr  ")]
    [InlineData("McMaster")]
    [InlineData("McMaster-Carr Supply Co.")]
    public void Vendor_spellings_are_McMaster(string vendor)
    {
        Assert.True(McMasterRule.IsMcMasterVendor(vendor));
        Assert.True(McMasterRule.IsMcMasterCad(vendor, @"C:\x\a.ipt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Misumi")]
    [InlineData("Master Lock")]
    [InlineData("Carr McMaster Ltd")]
    public void Other_vendors_are_not_McMaster(string? vendor) => Assert.False(McMasterRule.IsMcMasterVendor(vendor));

    [Theory]
    [InlineData("$/DESIGNS/Simplifyber Library/McMaster-Carr/Screws/91251A537.ipt")]
    [InlineData("$/designs/simplifyber library/mcmaster-carr/91251A537.ipt")]
    [InlineData(@"C:\_Vault Working Folder\Designs\Simplifyber Library\McMaster-Carr\Screws\91251A537.ipt")]
    [InlineData("$/DESIGNS/Simplifyber Library/McMaster-Carr")]
    public void Library_path_is_McMaster(string path)
    {
        Assert.True(McMasterRule.IsMcMasterPath(path));
        Assert.True(McMasterRule.IsMcMasterCad("", path));
    }

    [Theory]
    [InlineData("$/DESIGNS/Simplifyber Library/Misumi/a.ipt")]
    [InlineData("$/DESIGNS/Simplifyber Library/McMaster-Carr Old/a.ipt")]
    [InlineData("$/DESIGNS/PRODUCTION EQUIPMENT/a.ipt")]
    [InlineData("")]
    public void Other_paths_are_not_McMaster(string path) => Assert.False(McMasterRule.IsMcMasterPath(path));

    [Fact]
    public void Neither_vendor_nor_path_is_not_McMaster() => Assert.False(McMasterRule.IsMcMasterCad(N("CD-1.ipt", vendor: "Misumi")));

    [Fact]
    public void Mis_categorised_McMaster_part_is_not_a_Make_part_in_any_mode()
    {
        // An old SF-numbered library part filed as Part - Engineering with a CNC process: still never a Make part.
        var part = N("SF-000100.ipt", vendor: "McMASTER-CARR", process: "CNC");
        Assert.Equal(PackageKind.None, PackageRules.Classify(part, "Part - Engineering", Mats, false).Kind);
        foreach (var mode in Enum.GetValues<ManufacturingMode>())
            Assert.Empty(PackageRules.Select(new[] { part }, mode, Mats, _ => "Part - Engineering"));
    }

    [Fact]
    public void Library_path_part_is_excluded_even_with_blank_vendor()
    {
        var part = N("SF-000101.ipt", folder: @"C:\_Vault Working Folder\Designs\Simplifyber Library\McMaster-Carr\");
        Assert.Empty(PackageRules.Select(new[] { part }, ManufacturingMode.AllMake, Mats, _ => null));
        Assert.Empty(PackageRules.Select(new[] { N("PLA.ipt", vendor: "McMaster-Carr", material: "PLA") }, ManufacturingMode.Print, Mats, _ => null));
    }

    [Fact]
    public void Selection_keeps_other_parts_and_lists_McMaster_rows()
    {
        var mc = N("SF-000100.ipt", vendor: "McMaster-Carr", vendorNumber: "91251A537");
        var own = N("CD-1.ipt");
        var cands = PackageRules.Candidates(new[] { own, mc }, Mats, _ => null);
        Assert.Equal(new[] { "CD-1" }, PackageRules.Select(new[] { own, mc }, ManufacturingMode.AllMake, Mats, _ => null).Select(p => p.Node.PartNumber));
        var row = Assert.Single(PackageRules.McMasterRows(cands));
        Assert.Equal(("SF-000100", "91251A537", McMasterRule.BomNote, 0), (row.PartNumber, row.VendorNumber, row.Note, row.Files.Count));
        Assert.Equal(2, row.Qty);
    }

    [Fact]
    public void Purchased_McMaster_part_is_listed_too_and_nodes_are_found_under_a_purchased_parent()
    {
        var inner = N("SF-000102.ipt", vendor: "McMaster-Carr");
        var kit = N("KIT-1.iam", structure: "Purchased", kids: inner);
        var buy = N("91251A537.ipt", vendor: "McMaster-Carr", structure: "Purchased");
        var cands = PackageRules.Candidates(new[] { kit, buy }, Mats, _ => null);
        Assert.Equal(new[] { "91251A537" }, PackageRules.McMasterRows(cands).Select(r => r.PartNumber));
        Assert.Equal(new[] { "91251A537", "SF-000102" }, PackageRules.McMasterNodes(new[] { kit, buy }).Select(n => n.PartNumber).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Top_assembly_from_McMaster_is_not_exported()
    {
        var top = N("TOP.iam", vendor: "McMaster-Carr");
        Assert.Empty(PackageRules.Select(Array.Empty<BomNode>(), ManufacturingMode.AllMake, Mats, _ => null, top));
    }

    [Fact]
    public void Selected_and_single_scope_skip_McMaster_with_the_terms_text()
    {
        var mc = N("SF-000100.ipt", vendor: "McMaster-Carr");
        var own = N("CD-1.ipt");
        var all = PackageRules.Candidates(new[] { own, mc }, Mats, _ => null);
        var pick = PackageScope.PickSelected(all, new[] { own.FullPath, mc.FullPath });
        Assert.Equal(new[] { "CD-1" }, pick.Candidates.Select(c => c.Node.PartNumber));
        Assert.Equal(new[] { PackageScope.McMasterSkip("SF-000100") }, pick.Skipped);
        Assert.Equal(new[] { "SF-000100" }, pick.McMasterParts.Select(c => c.Node.PartNumber));
        var single = PackageScope.PickPart(PackageRules.Candidates(new[] { mc }, Mats, _ => null));
        Assert.Empty(single.Candidates);
        Assert.Equal(new[] { PackageScope.McMasterSkip("SF-000100") }, single.Skipped);
        Assert.Single(single.McMasterParts);
    }

    [Fact]
    public void Left_out_text_names_the_parts_once()
    {
        Assert.Equal("Left out: McMaster-Carr CAD (supplier terms): A, B", McMasterRule.LeftOutText(new[] { "A", "a", "B", "" }));
        Assert.Equal("", McMasterRule.LeftOutText(Array.Empty<string>()));
    }

    [Fact]
    public void Manifest_carries_left_out_and_row_note()
    {
        var mc = PackageRules.McMasterRows(PackageRules.Candidates(new[] { N("SF-000100.ipt", vendor: "McMaster-Carr", vendorNumber: "91251A537") }, Mats, _ => null));
        var json = JsonNode.Parse(PackageManifest.ToJson(new PackageManifestData("partner", "CD-1", "d", "A", "Released", "Acme", "Make", "", "u",
            new DateTime(2026, 10, 14), mc, Array.Empty<PackageFile>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<PackageFailure>(), LeftOut: new[] { "SF-000100" })))!;
        Assert.Equal("SF-000100", json["left_out"]![0]!.GetValue<string>());
        Assert.Equal(McMasterRule.BomNote, json["rows"]![0]!["note"]!.GetValue<string>());
        Assert.Equal("91251A537", json["rows"]![0]!["vendor_number"]!.GetValue<string>());
    }

    [Fact]
    public void Step_leak_check_finds_McMaster_names_only_on_product_lines()
    {
        var lines = new[]
        {
            "#10=PRODUCT('CD-1','CD-1','',(#2));",
            "#11=PRODUCT('SF-000100','SF-000100','',(#2));",
            "#12=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','91251A537:1','',#10,#11,$);",
            "#13=COMMENT('91251A537 appears in a header');",
        };
        Assert.Equal(new[] { "SF-000100", "91251A537" }, McMasterRule.FindLeaks(lines, new[] { "SF-000100", "91251A537", "NOPE-1" }));
        Assert.Empty(McMasterRule.FindLeaks(lines.Take(1), new[] { "SF-000100" }));
        Assert.Empty(McMasterRule.FindLeaks(lines, Array.Empty<string>()));
    }
}
