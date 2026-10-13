using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class PackageRulesTests
{
    private static readonly PrintMaterials Mats = new(BridgeSettings.Defaults.PrintMaterials);

    private static BomNode N(string file, double qty = 1, string structure = "Normal", string process = "", string material = "Steel",
                             bool sheet = false, string rev = "A", string state = "Released", params BomNode[] kids) =>
        new(Path.GetFileNameWithoutExtension(file), "d " + file, qty, "Each", structure, rev, material, file,
            @"C:\Vault Workspace\DESIGNS\" + file, kids, State: state, Process: process, IsSheetMetal: sheet);

    private static PackagePart Part(string file, PackageClass cls, PackageFiles files, double qty = 1, string material = "Steel", string rev = "A", string process = "") =>
        new("1", N(file, material: material, rev: rev, process: process), cls, files, qty);

    [Theory]
    [InlineData("CNC", PackageKind.Cnc)]
    [InlineData(" cnc ", PackageKind.Cnc)]
    [InlineData("Sheet Metal", PackageKind.SheetMetal)]
    [InlineData("sheet metal", PackageKind.SheetMetal)]
    [InlineData("3D Print", PackageKind.Print)]
    [InlineData("Weldment", PackageKind.OtherMake)]
    [InlineData("In-house", PackageKind.OtherMake)]
    [InlineData("Other", PackageKind.OtherMake)]
    [InlineData("Laser", PackageKind.OtherMake)]
    public void Classify_process_wins_and_is_not_guessed(string process, PackageKind kind)
    {
        var c = PackageRules.Classify(N("CD-1.ipt", process: process, material: "PLA", sheet: true), null, Mats, true);
        Assert.Equal(new PackageClass(kind, false), c);
    }

    [Fact]
    public void Classify_blank_sheet_metal_is_guessed_sheet_metal() =>
        Assert.Equal(new PackageClass(PackageKind.SheetMetal, true), PackageRules.Classify(N("CD-1.ipt", material: "PLA", sheet: true), null, Mats, true));

    [Fact]
    public void Classify_blank_print_material_is_guessed_print() =>
        Assert.Equal(new PackageClass(PackageKind.Print, true), PackageRules.Classify(N("CD-1.ipt", material: "PETG Black"), null, Mats, false));

    [Fact]
    public void Classify_blank_ipt_is_guessed_cnc() =>
        Assert.Equal(new PackageClass(PackageKind.Cnc, true), PackageRules.Classify(N("CD-1.ipt", material: "Aluminum 6061"), null, Mats, false));

    [Fact]
    public void Classify_blank_iam_is_guessed_other() =>
        Assert.Equal(new PackageClass(PackageKind.OtherMake, true), PackageRules.Classify(N("CD-2.iam"), null, Mats, false));

    [Fact]
    public void Classify_none_cases()
    {
        var none = new PackageClass(PackageKind.None, false);
        Assert.Equal(none, PackageRules.Classify(N("CD-1.ipt", structure: "Purchased", process: "CNC"), null, Mats, false));
        Assert.Equal(none, PackageRules.Classify(N("CD-1.ipt", structure: "Phantom", process: "CNC"), null, Mats, false));
        Assert.Equal(none, PackageRules.Classify(N("CD-1.ipt", structure: "Reference", process: "CNC"), null, Mats, false));
        Assert.Equal(none, PackageRules.Classify(N("CD-1.ipt", process: "CNC"), "Part - Purchased", Mats, false));
        Assert.Equal(none, PackageRules.Classify(N("CD-1.ipt", process: "CNC"), "Part - Content Center", Mats, false));
        var cc = N("CD-1.ipt", process: "CNC") with { FullPath = @"C:\Vault Workspace\Content Center Files\x\CD-1.ipt" };
        Assert.Equal(none, PackageRules.Classify(cc, null, Mats, false));
        var virt = N("CD-1.ipt", process: "CNC") with { FileName = "", FullPath = "" };
        Assert.Equal(none, PackageRules.Classify(virt, null, Mats, false));
    }

    private const PackageFiles SP = PackageFiles.Step | PackageFiles.Pdf;

    [Theory]
    [InlineData(ManufacturingMode.AllMake, PackageKind.None, PackageFiles.None)]
    [InlineData(ManufacturingMode.AllMake, PackageKind.Cnc, SP)]
    [InlineData(ManufacturingMode.AllMake, PackageKind.SheetMetal, SP | PackageFiles.Dxf)]
    [InlineData(ManufacturingMode.AllMake, PackageKind.Print, SP)]
    [InlineData(ManufacturingMode.AllMake, PackageKind.OtherMake, SP)]
    [InlineData(ManufacturingMode.Cnc, PackageKind.None, PackageFiles.None)]
    [InlineData(ManufacturingMode.Cnc, PackageKind.Cnc, SP)]
    [InlineData(ManufacturingMode.Cnc, PackageKind.SheetMetal, PackageFiles.None)]
    [InlineData(ManufacturingMode.Cnc, PackageKind.Print, PackageFiles.None)]
    [InlineData(ManufacturingMode.Cnc, PackageKind.OtherMake, PackageFiles.None)]
    [InlineData(ManufacturingMode.SheetMetal, PackageKind.None, PackageFiles.None)]
    [InlineData(ManufacturingMode.SheetMetal, PackageKind.Cnc, PackageFiles.None)]
    [InlineData(ManufacturingMode.SheetMetal, PackageKind.SheetMetal, PackageFiles.Dxf | PackageFiles.Pdf)]
    [InlineData(ManufacturingMode.SheetMetal, PackageKind.Print, PackageFiles.None)]
    [InlineData(ManufacturingMode.SheetMetal, PackageKind.OtherMake, PackageFiles.None)]
    [InlineData(ManufacturingMode.Print, PackageKind.None, PackageFiles.None)]
    [InlineData(ManufacturingMode.Print, PackageKind.Cnc, PackageFiles.None)]
    [InlineData(ManufacturingMode.Print, PackageKind.SheetMetal, PackageFiles.None)]
    [InlineData(ManufacturingMode.Print, PackageKind.Print, PackageFiles.Print)]
    [InlineData(ManufacturingMode.Print, PackageKind.OtherMake, PackageFiles.None)]
    public void FilesFor_table(ManufacturingMode mode, PackageKind kind, PackageFiles expected) =>
        Assert.Equal(expected, PackageRules.FilesFor(mode, kind));

    [Fact]
    public void Select_dedupes_and_totals_quantities()
    {
        var roots = new[]
        {
            N("CD-100.iam", 2, kids: new[] { N("CD-200.ipt", 3), N("CD-300.ipt", 1, process: "Sheet Metal") }),
            N("CD-200.ipt", 4),
        };
        var parts = PackageRules.Select(roots, ManufacturingMode.Cnc, Mats, _ => null);
        var p = Assert.Single(parts);
        Assert.Equal("CD-200.ipt", p.Node.FileName);
        Assert.Equal(10, p.TotalQty);
        Assert.Equal(BomOrdering.Flatten(roots).First(l => l.Node.FileName == "CD-200.ipt").Item, p.Item);
    }

    [Fact]
    public void Select_prunes_children_of_buy_and_reference_rows_not_phantom()
    {
        var roots = new[]
        {
            N("CD-400.iam", structure: "Purchased", kids: new[] { N("CD-500.ipt") }),
            N("CD-410.iam", structure: "Reference", kids: new[] { N("CD-510.ipt") }),
            N("CD-420.iam", structure: "Phantom", kids: new[] { N("CD-520.ipt", 2) }),
            N("CD-430.iam", kids: new[] { N("CD-530.ipt") }),
        };
        var parts = PackageRules.Select(roots, ManufacturingMode.AllMake, Mats, n => n.FileName == "CD-430.iam" ? "Part - Purchased" : null);
        Assert.Equal(new[] { "CD-520.ipt" }, parts.Select(p => p.Node.FileName));
        Assert.Equal(2, parts[0].TotalQty);
    }

    [Fact]
    public void Select_all_make_prepends_top()
    {
        var top = N("CD-001882.iam");
        var roots = new[] { N("CD-200.ipt"), N("CD-300.ipt", sheet: true) };
        var parts = PackageRules.Select(roots, ManufacturingMode.AllMake, Mats, _ => null, top);
        Assert.Equal(PackageRules.TopItem, parts[0].Item);
        Assert.Equal(PackageFiles.Step | PackageFiles.Pdf, parts[0].Files);
        Assert.Equal(new PackageClass(PackageKind.OtherMake, true), parts[0].Class);
        Assert.Equal(PackageFiles.Step | PackageFiles.Pdf | PackageFiles.Dxf, parts.Single(p => p.Node.FileName == "CD-300.ipt").Files);
        var cnc = PackageRules.Select(roots, ManufacturingMode.Cnc, Mats, _ => null, top);
        Assert.DoesNotContain(cnc, p => p.Item == PackageRules.TopItem);
    }

    [Fact]
    public void Select_print_mode()
    {
        var roots = new[] { N("CD-600.ipt", 3, material: "PLA"), N("CD-601.ipt", process: "3D Print", material: "Steel"), N("CD-602.ipt") };
        var parts = PackageRules.Select(roots, ManufacturingMode.Print, Mats, _ => null);
        Assert.Equal(new[] { "CD-600.ipt", "CD-601.ipt" }.OrderBy(x => x), parts.Select(p => p.Node.FileName).OrderBy(x => x));
        Assert.All(parts, p => Assert.Equal(PackageFiles.Print, p.Files));
    }

    [Theory]
    [InlineData("PLA", true)]
    [InlineData("pla+", true)]
    [InlineData("PC-ABS", true)]
    [InlineData("Onyx (PA12-CF)", true)]
    [InlineData("Nylon 12", true)]
    [InlineData("TPU 95A", true)]
    [InlineData("Tough Resin", true)]
    [InlineData("PC", true)]
    [InlineData("Generic Plastic", false)]
    [InlineData("PCB laminate", false)]
    [InlineData("Polycarbonate", false)]
    [InlineData("Carbon Fiber Sheet", false)]
    [InlineData("Steel, Mild", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PrintMaterials_word_boundaries(string? material, bool expected) =>
        Assert.Equal(expected, Mats.Matches(material));

    [Fact]
    public void FileStem_and_dests()
    {
        var p = Part("CD-000123.ipt", new PackageClass(PackageKind.Cnc, false), SP);
        Assert.Equal("CD-000123 Rev A", PackageRules.FileStem(p.Node));
        Assert.Equal("STEP/CD-000123 Rev A.stp", PackageRules.Dest(PackageFiles.Step, p));
        Assert.Equal("PDF/CD-000123 Rev A.pdf", PackageRules.Dest(PackageFiles.Pdf, p));
        Assert.Equal("DXF/CD-000123 Rev A.dxf", PackageRules.Dest(PackageFiles.Dxf, p));
        Assert.Equal("CD-000123 Rev -", PackageRules.FileStem(Part("CD-000123.ipt", default, SP, rev: "").Node));
        Assert.Equal("CD-000123 Rev A-B", PackageRules.FileStem(Part("CD-000123.ipt", default, SP, rev: "A/B").Node));
    }

    [Fact]
    public void PrintDest_folder_per_material()
    {
        var p = Part("CD-000123.ipt", new PackageClass(PackageKind.Print, false), PackageFiles.Print, qty: 4, material: "PLA: Black");
        Assert.Equal("PLA- Black/CD-000123 Rev A x4.3mf", PackageRules.PrintDest(p, ".3mf"));
        var q = Part("CD-000123.ipt", new PackageClass(PackageKind.Print, false), PackageFiles.Print, qty: 2.5, material: "PLA: Black");
        Assert.EndsWith(" x2.5.stl", PackageRules.PrintDest(q, "stl"));
        var blank = Part("CD-000123.ipt", new PackageClass(PackageKind.Print, false), PackageFiles.Print, material: "");
        Assert.StartsWith("No material/", PackageRules.PrintDest(blank, "stl"));
        Assert.Equal("No material", PackageRules.MaterialFolder(null));
    }

    [Theory]
    [InlineData(null, "Partner is required.")]
    [InlineData("", "Partner is required.")]
    [InlineData("  ", "Partner is required.")]
    [InlineData("Acme/West", "Partner: use a name without / \\ : * ? \" < > | and no trailing dot.")]
    [InlineData("Acme.", "Partner: use a name without / \\ : * ? \" < > | and no trailing dot.")]
    [InlineData("A|B", "Partner: use a name without / \\ : * ? \" < > | and no trailing dot.")]
    [InlineData("Acme Machining", null)]
    public void PartnerError_cases(string? partner, string? expected) =>
        Assert.Equal(expected, PackageRules.PartnerError(partner));

    [Fact]
    public void PartnerError_length() =>
        Assert.Equal("Partner: 60 characters at most.", PackageRules.PartnerError(new string('a', 61)));

    [Fact]
    public void PartnerZipName_sanitizes()
    {
        Assert.Equal("CD-001882 Acme Machining CNC Package.zip", PackageRules.PartnerZipName("CD-001882", "Acme Machining", ManufacturingMode.Cnc));
        Assert.Equal("CD-001882 Acme Machining Make Package.zip", PackageRules.PartnerZipName("CD-001882", "Acme Machining", ManufacturingMode.AllMake));
        Assert.Equal("CD-001882 Acme Machining Sheet Metal Package.zip", PackageRules.PartnerZipName("CD-001882", "Acme Machining", ManufacturingMode.SheetMetal));
        Assert.Equal("CD-001882 A-B CNC Package.zip", PackageRules.PartnerZipName("CD-001882", "A:B", ManufacturingMode.Cnc));
        Assert.Equal("CD-001882 3D Print Package.zip", PackageRules.PrintZipName("CD-001882"));
    }

    [Fact]
    public void PreviewText_counts()
    {
        var top = N("CD-001882.iam");
        var roots = new[] { N("CD-200.ipt"), N("CD-300.ipt", sheet: true) };
        var all = PackageRules.Select(roots, ManufacturingMode.AllMake, Mats, _ => null, top);
        Assert.Equal("3 parts: 3 STEP, 3 PDF, 1 DXF", PackageRules.PreviewText(all, ManufacturingMode.AllMake));
        var one = new[] { Part("CD-200.ipt", new PackageClass(PackageKind.Cnc, false), SP) };
        Assert.Equal("1 part: 1 STEP, 1 PDF, 0 DXF", PackageRules.PreviewText(one, ManufacturingMode.Cnc));
        var two = new[]
        {
            Part("CD-1.ipt", new PackageClass(PackageKind.Print, false), PackageFiles.Print),
            Part("CD-2.ipt", new PackageClass(PackageKind.Print, false), PackageFiles.Print),
        };
        Assert.Equal("2 parts: 2 print files", PackageRules.PreviewText(two, ManufacturingMode.Print));
        Assert.Equal("1 part: 1 print file", PackageRules.PreviewText(two.Take(1).ToList(), ManufacturingMode.Print));
    }

    [Fact]
    public void PreviewText_for_empty_selection()
    {
        var none = Array.Empty<PackagePart>();
        Assert.Equal("No Make parts found.", PackageRules.PreviewText(none, ManufacturingMode.AllMake));
        Assert.Equal("No CNC parts found.", PackageRules.PreviewText(none, ManufacturingMode.Cnc));
        Assert.Equal("No sheet metal parts found.", PackageRules.PreviewText(none, ManufacturingMode.SheetMetal));
        Assert.Equal("No 3D print parts found.", PackageRules.PreviewText(none, ManufacturingMode.Print));
    }

    [Fact]
    public void ProcessText_and_guessed_entry()
    {
        var set = Part("CD-000123.ipt", new PackageClass(PackageKind.Cnc, false), SP, process: " CNC ");
        Assert.Equal("CNC", PackageRules.ProcessText(set));
        var g = Part("CD-000123.ipt", new PackageClass(PackageKind.Cnc, true), SP);
        Assert.Equal("guessed: CNC", PackageRules.ProcessText(g));
        Assert.Equal("guessed: Other make", PackageRules.ProcessText(Part("CD-2.iam", new PackageClass(PackageKind.OtherMake, true), SP)));
        Assert.Equal("CD-000123 (CNC)", PackageRules.GuessedEntry(g));
    }

    [Theory]
    [InlineData("Released", true)]
    [InlineData(" released ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Work in Progress", false)]
    public void IsReleased(string? state, bool expected) => Assert.Equal(expected, PackageRules.IsReleased(state));

    [Fact]
    public void QtyText_is_invariant()
    {
        Assert.Equal("4", PackageRules.QtyText(4));
        Assert.Equal("2.5", PackageRules.QtyText(2.5));
    }

    [Fact]
    public void Manifest_json_contract()
    {
        var m = new PackageManifestData("partner", "CD-001882", "Big blue adapter", "A", "Released", "Acme Machining", "CNC",
            "Model state: Primary", "zak", new DateTime(2026, 10, 13, 14, 5, 0),
            new[] { new PackageRow("1.2", "CD-000123", "Plate", 4, "Aluminum 6061", "As Finished", "guessed: CNC", "A", "Work in Progress", new[] { "STEP/a.stp", "PDF/a.pdf" }) },
            new[] { new PackageFile(@"C:\tmp\001.pdf", "PDF/a.pdf", true) },
            new[] { "CD-000123 (CNC)" }, new[] { "CD-000124" }, new[] { "CD-000125" },
            new[] { new PackageFailure("CD-000126", "STEP export failed") });
        var o = JsonNode.Parse(PackageManifest.ToJson(m))!.AsObject();
        Assert.Equal("partner", (string?)o["kind"]);
        Assert.Equal("CD-001882", (string?)o["assembly"]);
        Assert.Equal("Big blue adapter", (string?)o["description"]);
        Assert.Equal("A", (string?)o["revision"]);
        Assert.Equal("Released", (string?)o["state"]);
        Assert.Equal("Acme Machining", (string?)o["partner"]);
        Assert.Equal("CNC", (string?)o["mode"]);
        Assert.Equal("Model state: Primary", (string?)o["source"]);
        Assert.Equal("zak", (string?)o["user"]);
        Assert.Equal("2026-10-13 14:05", (string?)o["created"]);
        var r = o["rows"]![0]!;
        Assert.Equal("1.2", (string?)r["item"]);
        Assert.Equal("CD-000123", (string?)r["part_number"]);
        Assert.Equal("Plate", (string?)r["description"]);
        Assert.Equal(4, r["qty"]!.GetValue<double>());
        Assert.Equal("Aluminum 6061", (string?)r["material"]);
        Assert.Equal("As Finished", (string?)r["finish"]);
        Assert.Equal("guessed: CNC", (string?)r["process"]);
        Assert.Equal("A", (string?)r["revision"]);
        Assert.Equal("Work in Progress", (string?)r["state"]);
        Assert.Equal(new[] { "STEP/a.stp", "PDF/a.pdf" }, r["files"]!.AsArray().Select(x => (string?)x));
        var f = o["files"]![0]!;
        Assert.Equal(@"C:\tmp\001.pdf", (string?)f["src"]);
        Assert.Equal("PDF/a.pdf", (string?)f["dest"]);
        Assert.True(f["stamp"]!.GetValue<bool>());
        Assert.Equal("CD-000123 (CNC)", (string?)o["guessed"]![0]);
        Assert.Equal("CD-000124", (string?)o["missing_drawings"]![0]);
        Assert.Equal("CD-000125", (string?)o["no_flat_pattern"]![0]);
        Assert.Equal("CD-000126", (string?)o["failed"]![0]!["part"]);
        Assert.Equal("STEP export failed", (string?)o["failed"]![0]!["reason"]);
    }

    [Fact]
    public void Manifest_writes_quote_flag_defaulting_to_false()
    {
        PackageManifestData Make(bool? quote) => quote is null
            ? new PackageManifestData("partner", "A", "", "", "", "P", "CNC", "", "u", new DateTime(2026, 10, 13),
                Array.Empty<PackageRow>(), Array.Empty<PackageFile>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<PackageFailure>())
            : new PackageManifestData("partner", "A", "", "", "", "P", "CNC", "", "u", new DateTime(2026, 10, 13),
                Array.Empty<PackageRow>(), Array.Empty<PackageFile>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<PackageFailure>(), quote.Value);
        Assert.False(JsonNode.Parse(PackageManifest.ToJson(Make(null)))!["quote"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(PackageManifest.ToJson(Make(true)))!["quote"]!.GetValue<bool>());
    }
}
