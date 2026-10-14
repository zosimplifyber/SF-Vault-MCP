using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class PackageScopeTests
{
    private static readonly PrintMaterials Mats = new(BridgeSettings.Defaults.PrintMaterials);
    private const string Root = @"C:\Vault Workspace\DESIGNS\";

    private static BomNode N(string file, double qty = 1, string structure = "Normal", string process = "CNC", bool sheet = false,
                             params BomNode[] kids) =>
        new(Path.GetFileNameWithoutExtension(file), "d " + file, qty, "Each", structure, "A", "Steel", file, Root + file, kids,
            State: "Released", Process: process, IsSheetMetal: sheet);

    private static IReadOnlyList<ScopeCandidate> Cands(params BomNode[] roots) => PackageRules.Candidates(roots, Mats, _ => null);

    [Theory]
    [InlineData(ScopeDoc.Assembly, 0, PackageScopeKind.Assembly)]
    [InlineData(ScopeDoc.Assembly, 1, PackageScopeKind.Selected)]
    [InlineData(ScopeDoc.Assembly, 5, PackageScopeKind.Selected)]
    [InlineData(ScopeDoc.Part, 0, PackageScopeKind.Part)]
    [InlineData(ScopeDoc.Part, 3, PackageScopeKind.Part)]
    public void Decide_by_document_and_selection(ScopeDoc doc, int selected, PackageScopeKind expected)
    {
        var d = PackageScope.Decide(doc, selected);
        Assert.Equal(expected, d.Kind);
        Assert.Null(d.Message);
    }

    [Theory]
    [InlineData(ScopeDoc.None)]
    [InlineData(ScopeDoc.Other)]
    public void Decide_drawing_or_nothing_open_says_open_an_assembly_or_a_part(ScopeDoc doc)
    {
        var d = PackageScope.Decide(doc, 2);
        Assert.Null(d.Kind);
        Assert.Equal("Open an assembly or a part.", d.Message);
    }

    [Fact]
    public void Candidates_total_quantity_matches_Select_and_keeps_non_make()
    {
        var top = N("CD-1.iam", 1, process: "", kids: new[]
        {
            N("CD-2.ipt", 2),
            N("CD-3.iam", 3, process: "", kids: new[] { N("CD-2.ipt", 4) }),
            N("CD-4.ipt", 1, structure: "Purchased"),
        });
        var cands = Cands(top);
        var two = cands.Single(c => c.Node.FileName == "CD-2.ipt");
        Assert.Equal(2 + 3 * 4, two.Total);
        Assert.Contains(cands, c => c.Node.FileName == "CD-4.ipt" && c.Class.Kind == PackageKind.None);
        var sel = PackageRules.Select(new[] { top }, ManufacturingMode.AllMake, Mats, _ => null);
        Assert.Equal(two.Total, sel.Single(p => p.Node.FileName == "CD-2.ipt").TotalQty);
    }

    [Fact]
    public void PickSelected_dedupes_by_path_across_proxy_and_occurrence_and_keeps_bom_order_description_descending()
    {
        var cands = Cands(N("CD-1.iam", process: "", kids: new[] { N("CD-2.ipt"), N("CD-3.ipt"), N("CD-5.ipt") }));
        var pick = PackageScope.PickSelected(cands, new string?[]
        {
            Root + "CD-5.ipt", Root.ToLowerInvariant() + "cd-5.ipt", (Root + "CD-2.ipt").Replace('\\', '/'), Root + "CD-5.ipt",
        });
        Assert.Equal(new[] { "CD-5.ipt", "CD-2.ipt" }, pick.Candidates.Select(c => c.Node.FileName));
        Assert.Empty(pick.Skipped);
    }

    [Fact]
    public void PickSelected_selected_sub_assembly_is_its_own_row_not_its_children()
    {
        var cands = Cands(N("CD-1.iam", process: "", kids: new[] { N("CD-9.iam", 2, process: "", kids: new[] { N("CD-2.ipt") }) }));
        var pick = PackageScope.PickSelected(cands, new string?[] { Root + "CD-9.iam" });
        var row = Assert.Single(pick.Candidates);
        Assert.Equal("CD-9.iam", row.Node.FileName);
        Assert.Equal(PackageFiles.Step | PackageFiles.Pdf, PackageRules.FilesFor(ManufacturingMode.AllMake, row.Class.Kind));
        Assert.Equal(2, PackageScope.DefaultQty(row.Total));
    }

    [Fact]
    public void PickSelected_skips_non_make_with_reason_once()
    {
        var cands = Cands(N("CD-1.iam", process: "", kids: new[]
        {
            N("CD-2.ipt"), N("CD-3.ipt", structure: "Purchased"), N("CD-4.ipt", structure: "Phantom"), N("CD-5.ipt", structure: "Reference"),
        }));
        var pick = PackageScope.PickSelected(cands, new string?[]
        {
            Root + "CD-3.ipt", Root + "CD-3.ipt", Root + "CD-4.ipt", Root + "CD-5.ipt", Root + "CD-2.ipt",
            @"C:\Vault Workspace\Content Center Files\x\Bolt.ipt", null, "",
        });
        Assert.Equal(new[] { "CD-2.ipt" }, pick.Candidates.Select(c => c.Node.FileName));
        Assert.Equal(new[]
        {
            "not a Make part: CD-3", "not a Make part: CD-4", "not a Make part: CD-5",
            "not a Make part: Bolt.ipt", "not a Make part: a component without a file (virtual)",
        }, pick.Skipped);
    }

    [Fact]
    public void PickSelected_content_center_row_in_bom_is_skipped()
    {
        var cc = N("Bolt.ipt") with { FullPath = @"C:\Vault Workspace\Content Center Files\x\Bolt.ipt" };
        var cands = Cands(N("CD-1.iam", process: "", kids: new[] { cc }));
        var pick = PackageScope.PickSelected(cands, new string?[] { cc.FullPath });
        Assert.Empty(pick.Candidates);
        Assert.Equal(new[] { "not a Make part: Bolt" }, pick.Skipped);
    }

    [Fact]
    public void PickPart_make_part_is_picked_and_purchased_is_skipped()
    {
        Assert.Single(PackageScope.PickPart(Cands(N("CD-6.ipt"))).Candidates);
        var buy = PackageScope.PickPart(Cands(N("CD-7.ipt", structure: "Purchased")));
        Assert.Empty(buy.Candidates);
        Assert.Equal(new[] { "not a Make part: CD-7" }, buy.Skipped);
    }

    [Theory]
    [InlineData("Purchased")]
    [InlineData("Reference")]
    [InlineData("Phantom")]
    public void Single_part_with_non_make_structure_is_not_a_make_part(string structure)
    {
        var pick = PackageScope.PickPart(Cands(N("CD-8.ipt", structure: structure)));
        Assert.Empty(pick.Candidates);
        Assert.Equal(new[] { "not a Make part: CD-8" }, pick.Skipped);
    }

    [Fact]
    public void Skipped_text_is_capped_and_scope_line_counts_skipped_picks()
    {
        var many = Enumerable.Range(1, 13).Select(i => $"x{i}").ToList();
        var lines = PackageScope.SkippedText(many).Split(Environment.NewLine);
        Assert.Equal(11, lines.Length);
        Assert.Equal("and 3 more", lines[^1]);
        Assert.Equal("3 selected parts in CD-1 (2 skipped)", PackageScope.ScopeLine(PackageScopeKind.Selected, "CD-1", 3, 2));
    }

    [Theory]
    [InlineData("100000", true)]
    [InlineData("100001", false)]
    public void ParseQty_has_an_upper_bound(string text, bool ok) => Assert.Equal(ok, PackageScope.ParseQty(text).Value is not null);

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(12.0, 12)]
    [InlineData(2.5, 3)]
    [InlineData(0.5, 1)]
    [InlineData(0.0, 1)]
    [InlineData(-3.0, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void DefaultQty_is_whole_and_at_least_one(double total, int expected) => Assert.Equal(expected, PackageScope.DefaultQty(total));

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 25 ", 25)]
    public void ParseQty_accepts_whole_numbers(string text, int expected) => Assert.Equal((expected, (string?)null), PackageScope.ParseQty(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1.5")]
    [InlineData("two")]
    [InlineData("99999999999")]
    [InlineData("2147483647")]
    public void ParseQty_rejects_everything_else(string? text)
    {
        var (value, error) = PackageScope.ParseQty(text);
        Assert.Null(value);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Rows_single_defaults_to_one_and_selected_to_total()
    {
        var cands = Cands(N("CD-1.iam", process: "", kids: new[] { N("CD-2.ipt", 4) }));
        var part = cands.Where(c => c.Node.FileName == "CD-2.ipt").ToList();
        Assert.Equal(4, PackageScope.Rows(part, ManufacturingMode.AllMake, single: false).Single().DefaultQty);
        Assert.Equal(1, PackageScope.Rows(part, ManufacturingMode.AllMake, single: true).Single().DefaultQty);
    }

    [Theory]
    [InlineData("CNC", ManufacturingMode.AllMake, true, null)]
    [InlineData("CNC", ManufacturingMode.Cnc, true, null)]
    [InlineData("CNC", ManufacturingMode.SheetMetal, false, "not sheet metal")]
    [InlineData("Sheet Metal", ManufacturingMode.Cnc, false, "not CNC")]
    [InlineData("Sheet Metal", ManufacturingMode.SheetMetal, true, null)]
    [InlineData("Sheet Metal", ManufacturingMode.AllMake, true, null)]
    public void Rows_mode_filter_reasons(string process, ManufacturingMode mode, bool included, string? note)
    {
        var cands = Cands(N("CD-2.ipt", process: process));
        var row = PackageScope.Rows(cands, mode, true).Single();
        Assert.Equal(included, row.Included);
        Assert.Equal(note, row.Note);
        if (!included) Assert.Equal(PackageFiles.None, row.Files);
    }

    [Fact]
    public void ToParts_uses_entered_quantities_and_drops_excluded_rows()
    {
        var cands = Cands(N("CD-1.iam", process: "", kids: new[] { N("CD-2.ipt", 2), N("CD-3.ipt", 1, process: "Sheet Metal") }));
        var rows = PackageScope.Rows(cands.Where(c => c.Node.FileName != "CD-1.iam").ToList(), ManufacturingMode.Cnc, false);
        var parts = PackageScope.ToParts(rows, r => r.Candidate.Node.FileName == "CD-2.ipt" ? 7 : null);
        var p = Assert.Single(parts);
        Assert.Equal(7, p.TotalQty);
        Assert.Equal(PackageFiles.Step | PackageFiles.Pdf, p.Files);
        Assert.Equal(2, PackageScope.ToParts(rows, _ => null).Single().TotalQty);
    }

    [Theory]
    [InlineData(PackageFiles.Step | PackageFiles.Pdf | PackageFiles.Dxf, "STEP/PDF/DXF")]
    [InlineData(PackageFiles.Dxf | PackageFiles.Pdf, "PDF/DXF")]
    [InlineData(PackageFiles.None, "")]
    public void FilesText_lists_modes(PackageFiles f, string text) => Assert.Equal(text, PackageScope.FilesText(f));

    [Theory]
    [InlineData(PackageScopeKind.Assembly, 0, "Whole assembly CD-001882")]
    [InlineData(PackageScopeKind.Selected, 3, "3 selected parts in CD-001882")]
    [InlineData(PackageScopeKind.Selected, 1, "1 selected part in CD-001882")]
    public void ScopeLine_assembly_and_selected(PackageScopeKind kind, int n, string text) =>
        Assert.Equal(text, PackageScope.ScopeLine(kind, "CD-001882", n));

    [Fact]
    public void ScopeLine_part() => Assert.Equal("Part CD-001885", PackageScope.ScopeLine(PackageScopeKind.Part, "CD-001885"));

    [Theory]
    [InlineData(PackageScopeKind.Assembly, false, "CD-001882 Acme CNC Package.zip")]
    [InlineData(PackageScopeKind.Assembly, true, "CD-001882 Acme CNC Quote Package.zip")]
    [InlineData(PackageScopeKind.Part, false, "CD-001882 Acme CNC Package.zip")]
    [InlineData(PackageScopeKind.Part, true, "CD-001882 Acme CNC Quote Package.zip")]
    [InlineData(PackageScopeKind.Selected, false, "CD-001882 Acme CNC Selected Package.zip")]
    [InlineData(PackageScopeKind.Selected, true, "CD-001882 Acme CNC Selected Quote Package.zip")]
    public void ZipName_all_scopes(PackageScopeKind kind, bool quote, string expected) =>
        Assert.Equal(expected, PackageScope.ZipName(kind, "CD-001882", " Acme ", ManufacturingMode.Cnc, quote));

    [Fact]
    public void ZipName_assembly_matches_existing_PartnerZipName() =>
        Assert.Equal(PackageRules.PartnerZipName("CD-1", "Acme", ManufacturingMode.AllMake, true),
                     PackageScope.ZipName(PackageScopeKind.Assembly, "CD-1", "Acme", ManufacturingMode.AllMake, true));

    private static PackageManifestData Manifest(string scope) =>
        new("partner", "CD-1", "d", "A", "Released", "Acme", "CNC", "src", "u", new DateTime(2026, 10, 13), Array.Empty<PackageRow>(),
            Array.Empty<PackageFile>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<PackageFailure>(),
            false, scope);

    [Fact]
    public void Manifest_scope_is_written_only_when_set()
    {
        Assert.Equal("Part CD-1", JsonNode.Parse(PackageManifest.ToJson(Manifest("Part CD-1")))!["scope"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(PackageManifest.ToJson(Manifest("")))!["scope"]);
    }
}
