using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class OutputsTests
{
    private static BomNode N(string pn, string structure = "Normal", string path = "", string vendor = "", string vendorNumber = "") =>
        new(pn, "d", 1, "Each", structure, "", "", pn + ".ipt", path == "" ? @"C:\Vault Workspace\DESIGNS\" + pn + ".ipt" : path, Array.Empty<BomNode>(), vendor, vendorNumber);

    [Fact]
    public void Maps_local_file_to_vault_folder()
    {
        Assert.Equal("$/DESIGNS/PRODUCTION EQUIPMENT",
            VaultPaths.ToVaultFolder(@"C:\Vault Workspace\DESIGNS\PRODUCTION EQUIPMENT\CD-001887.iam", @"C:\Vault Workspace"));
        Assert.Equal("$", VaultPaths.ToVaultFolder(@"C:\Vault Workspace\a.iam", @"C:\Vault Workspace\"));
    }

    [Fact]
    public void Outside_workspace_asks_to_check_in()
    {
        var ex = Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Users\z\Desktop\a.iam", @"C:\Vault Workspace"));
        Assert.Equal("not_in_vault", ex.Code);
        Assert.Contains("Check the assembly in to Vault first", ex.Message);
        Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Vault WorkspaceX\a.iam", @"C:\Vault Workspace"));
    }

    [Fact]
    public void Output_names()
    {
        Assert.Equal("CD-001887 Purchasing.xlsx", VaultPaths.PurchasingName(@"C:\x\CD-001887.iam"));
        Assert.Equal("CD-001887 Design Review.pdf", VaultPaths.ReviewName(@"C:\x\CD-001887.iam"));
    }

    [Fact]
    public void Saved_copy_name_gets_a_counter_when_taken()
    {
        Assert.Equal("a b.pdf", VaultPaths.UniqueName("a b.pdf", _ => false));
        var taken = new HashSet<string> { "a b.pdf", "a b (1).pdf" };
        Assert.Equal("a b (2).pdf", VaultPaths.UniqueName("a b.pdf", taken.Contains));
    }

    [Fact]
    public void Classify_ignored_purchased_and_make()
    {
        Assert.Equal(PartKind.Ignored, ReviewRules.Classify(N("A", "Phantom"), null));
        Assert.Equal(PartKind.Ignored, ReviewRules.Classify(N("A", "Reference"), "Part - Purchased"));
        Assert.Equal(PartKind.Ignored, ReviewRules.Classify(N("A", "Purchased"), "Part - Content Center"));
        Assert.Equal(PartKind.Ignored, ReviewRules.Classify(N("A"), "Part - Content Center"));
        Assert.Equal(PartKind.Ignored, ReviewRules.Classify(N("A", "Purchased", path: @"C:\Vault Workspace\Content Center Files\en-US\x.ipt"), null));
        Assert.Equal(PartKind.Purchased, ReviewRules.Classify(N("A", "Purchased"), null));
        Assert.Equal(PartKind.Purchased, ReviewRules.Classify(N("A"), "Part - Purchased"));
        Assert.Equal(PartKind.Make, ReviewRules.Classify(N("A"), "Part - Engineering"));
        Assert.Equal(PartKind.Make, ReviewRules.Classify(N("A", "Inseparable"), null));
    }

    private static BomNode F(string pn, string fileName, string structure = "Normal") =>
        new(pn, "d", 1, "Each", structure, "", "", fileName, @"C:\Vault Workspace\DESIGNS\" + fileName, Array.Empty<BomNode>(), "", "");

    [Fact]
    public void Assembly_inclusion_only_for_made_assemblies()
    {
        Assert.True(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam"), "Part - Engineering"));
        Assert.True(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.IAM"), null));
        Assert.True(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam", "Inseparable"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("P-1", "P-1.ipt"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("P-1", "P-1.idw"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam.bak"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam", "Phantom"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam", "Reference"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam"), "Part - Content Center"));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam", "Purchased"), null));
        Assert.False(ReviewRules.IncludeInAssemblyPackage(F("SUB-1", "SUB-1.iam"), "Part - Purchased"));
    }

    [Fact]
    public void Assembly_package_name()
    {
        Assert.Equal("CD-001887 Assembly Package.pdf", VaultPaths.AssemblyPackageName(@"C:\x\CD-001887.iam"));
    }

    [Fact]
    public void Manifest_mode_revision_and_drawing_state()
    {
        var json = ReviewManifest.ToJson("CD-001887", "desc", "zak", "Work in Progress", "5", new DateTime(2026, 10, 12, 17, 30, 0),
            new[] { new ReviewDrawing("CD-1", "CD-1.idw", @"C:\t\1.pdf", "Released"), new ReviewDrawing("CD-2", "CD-2.idw", @"C:\t\2.pdf") },
            Array.Empty<string>(), Array.Empty<ReviewFailure>(), Array.Empty<ReviewPurchased>(), Array.Empty<string>(),
            PackageMode.Assembly, "3");
        var o = (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal("assembly", o["mode"]!.GetValue<string>());
        Assert.Equal("3", o["revision"]!.GetValue<string>());
        Assert.Equal("Released", o["drawings"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("", o["drawings"]![1]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Manifest_writes_drawing_note_only_when_present()
    {
        var o = (JsonObject)JsonNode.Parse(ReviewManifest.ToJson("A", "", "", "", "", DateTime.Now,
            new[] { new ReviewDrawing("CD-1", "CD-1.idw", "p", "", "open in Inventor"), new ReviewDrawing("CD-2", "CD-2.idw", "q") },
            Array.Empty<string>(), Array.Empty<ReviewFailure>(), Array.Empty<ReviewPurchased>(), Array.Empty<string>()))!;
        Assert.Equal("open in Inventor", o["drawings"]![0]!["note"]!.GetValue<string>());
        Assert.False(((JsonObject)o["drawings"]![1]!).ContainsKey("note"));
    }

    [Fact]
    public void Tool_result_parses_mode()
    {
        Assert.Equal("assembly", ToolResults.Parse("{\"ok\": true, \"pdf\": \"a.pdf\", \"pages\": 1, \"mode\": \"assembly\"}", "pdf", "pages").Mode);
        Assert.Null(ToolResults.Parse("{\"ok\": true, \"pdf\": \"a.pdf\", \"pages\": 1}", "pdf", "pages").Mode);
    }

    [Fact]
    public void Manifest_defaults_to_review_mode_with_empty_revision()
    {
        var o = (JsonObject)JsonNode.Parse(ReviewManifest.ToJson("A", "", "", "", "", DateTime.Now,
            Array.Empty<ReviewDrawing>(), Array.Empty<string>(), Array.Empty<ReviewFailure>(), Array.Empty<ReviewPurchased>(), Array.Empty<string>()))!;
        Assert.Equal("review", o["mode"]!.GetValue<string>());
        Assert.Equal("", o["revision"]!.GetValue<string>());
    }

    [Fact]
    public void Vendor_pdf_rules()
    {
        Assert.Equal("a.pdf", ReviewRules.FirstPdf(new[] { "n.txt", "a.pdf", "b.PDF" }));
        Assert.Null(ReviewRules.FirstPdf(new[] { "n.txt", "CD-9.idw" }));
        var names = new[] { "notes.txt", "other.pdf", "CD-5 McMaster 91251A.pdf", "CD-5 datasheet.pdf" };
        Assert.Equal("CD-5 datasheet.pdf", ReviewRules.MatchVendorPdf("CD-5", "", names));
        Assert.Equal("x 91251A y.PDF", ReviewRules.MatchVendorPdf("CD-9", "91251A", new[] { "a.pdf", "x 91251A y.PDF" }));
        Assert.Null(ReviewRules.MatchVendorPdf("CD-9", "", new[] { "CD-9.idw", "zz.pdf" }));
        Assert.Null(ReviewRules.MatchVendorPdf("", "", new[] { "zz.pdf" }));
    }

    [Fact]
    public void Vendor_pdf_stem_needs_a_boundary()
    {
        Assert.Null(ReviewRules.MatchVendorPdf("CD-1", "", new[] { "CD-10 McMaster 123.pdf" }));
        Assert.Equal("CD-1 McMaster 90510A313.pdf", ReviewRules.MatchVendorPdf("CD-1", "", new[] { "CD-1 McMaster 90510A313.pdf" }));
        Assert.Equal("CD-1.pdf", ReviewRules.MatchVendorPdf("cd-1", "", new[] { "CD-1.pdf" }));
        Assert.Equal("CD-1_x.pdf", ReviewRules.MatchVendorPdf("CD-1", "", new[] { "CD-1_x.pdf" }));
    }

    [Fact]
    public void Vendor_number_must_be_a_whole_token()
    {
        Assert.Null(ReviewRules.MatchVendorPdf("CD-9", "90510A31", new[] { "x 90510A313.pdf" }));
        Assert.Null(ReviewRules.MatchVendorPdf("CD-9", "0510A313", new[] { "x 90510A313.pdf" }));
        Assert.Equal("x 90510A313.pdf", ReviewRules.MatchVendorPdf("CD-9", "90510A313", new[] { "x 90510A313.pdf" }));
    }

    [Fact]
    public void Vendor_pdf_stem_beats_vendor_number_and_ties_are_deterministic()
    {
        Assert.Equal("CD-1 notes.pdf", ReviewRules.MatchVendorPdf("CD-1", "555", new[] { "a 555.pdf", "CD-1 notes.pdf" }));
        Assert.Equal("CD-1 a.pdf", ReviewRules.MatchVendorPdf("CD-1", "", new[] { "CD-1 bb.pdf", "CD-1 b.pdf", "CD-1 a.pdf" }));
    }

    [Fact]
    public void Unique_components_keep_first_occurrence()
    {
        var a = N("A"); var b = N("B");
        var lines = new[] { new BomLine("1", a, 0), new BomLine("2", b, 0), new BomLine("2.1", a, 1) };
        Assert.Equal(new[] { "1", "2" }, ReviewRules.UniqueComponents(lines).Select(l => l.Item));
    }

    [Fact]
    public void Drawing_choice()
    {
        Assert.Equal(new[] { "CD-1.idw" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-9.idw", "CD-1.dwg", "CD-1.idw" }));
        Assert.Equal(new[] { "CD-1.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.dwg", "X.iam" }));
        Assert.Equal(new[] { "A.idw", "B.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "B.dwg", "A.idw", "A.IDW" }));
        Assert.Empty(ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.pdf" }));
        Assert.Equal("CD-1 - CD-1.idw", ReviewRules.Bookmark("CD-1", "CD-1.idw"));
    }

    [Fact]
    public void Manifest_json_matches_python_contract()
    {
        var json = ReviewManifest.ToJson("CD-001887", "desc", "zak", "Work in Progress", "5", new DateTime(2026, 10, 12, 17, 30, 0),
            new[] { new ReviewDrawing("CD-1", "CD-1.idw", @"C:\t\1.pdf") }, new[] { "CD-2" }, new[] { new ReviewFailure("CD-3.idw", "could not open") },
            new[] { new ReviewPurchased("CD-5", "MCMASTER-CARR", "91251A", @"C:	\p.pdf") }, new[] { "CD-6" });
        var o = (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal("2026-10-12 17:30", o["created"]!.GetValue<string>());
        Assert.Equal("CD-1.idw", o["drawings"]![0]!["drawing"]!.GetValue<string>());
        Assert.Equal(@"C:\t\1.pdf", o["drawings"]![0]!["pdf"]!.GetValue<string>());
        Assert.Equal("CD-1", o["drawings"]![0]!["part_number"]!.GetValue<string>());
        Assert.Equal("CD-2", o["missing"]![0]!.GetValue<string>());
        Assert.Equal("could not open", o["failed"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal("CD-5", o["purchased"]![0]!["part_number"]!.GetValue<string>());
        Assert.Equal("MCMASTER-CARR", o["purchased"]![0]!["vendor"]!.GetValue<string>());
        Assert.Equal("91251A", o["purchased"]![0]!["vendor_number"]!.GetValue<string>());
        Assert.Equal(@"C:	\p.pdf", o["purchased"]![0]!["pdf"]!.GetValue<string>());
        Assert.Equal("CD-6", o["no_vendor_drawing"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Tool_result_parsing()
    {
        var ok = ToolResults.Parse("{\"ok\": true, \"workbook\": \"C:\\\\a.xlsx\", \"lines\": 12, \"unmatched\": [\"X\"]}", "workbook", "lines");
        Assert.True(ok.Ok); Assert.Equal(@"C:\a.xlsx", ok.Output); Assert.Equal(12, ok.Count); Assert.Equal(new[] { "X" }, ok.Unmatched);
        var bad = ToolResults.Parse("{\"ok\": false, \"error\": \"No part numbers\"}", "workbook", "lines");
        Assert.False(bad.Ok); Assert.Equal("No part numbers", bad.Error);
        Assert.Null(ok.Note);
        var noted = ToolResults.Parse("{\"ok\": true, \"workbook\": \"a\", \"lines\": 1, \"reference_note\": \"Prices from the purchasing Microsoft List.\"}", "workbook", "lines");
        Assert.Equal("Prices from the purchasing Microsoft List.", noted.Note);
        Assert.Equal(0, ok.Unreleased);
        var pkg = ToolResults.Parse("{\"ok\": true, \"pdf\": \"a.pdf\", \"pages\": 9, \"mode\": \"assembly\", \"unreleased\": 2}", "pdf", "pages");
        Assert.Equal(9, pkg.Count); Assert.Equal(2, pkg.Unreleased);
        Assert.False(ToolResults.Parse(null, "pdf", "pages").Ok);
        Assert.Contains("bridge.log", ToolResults.Parse("not json", "pdf", "pages").Error);
    }

    [Fact]
    public void Tool_arguments()
    {
        Assert.Equal(new[] { "purchasing", "--bom", "b", "--assembly", "A", "--out", "o", "--name", "n", "--result", "r" },
            ToolResults.PurchasingArgs("b", "A", "o", "n", "r"));
        Assert.Equal(new[] { "purchasing", "--bom", "b", "--assembly", "A", "--out", "o", "--name", "n", "--result", "r", "--source", "S" },
            ToolResults.PurchasingArgs("b", "A", "o", "n", "r", "S"));
        Assert.Equal(new[] { "review", "--bom", "b", "--manifest", "m", "--out", "p", "--result", "r" },
            ToolResults.ReviewArgs("b", "m", "p", "r"));
    }

    [Fact]
    public void Bom_source_line()
    {
        Assert.Equal("Model state: Primary | Design view: Default | BOM view: Structured", BomSource.Describe("Primary", "Default"));
        Assert.Equal("Model state: (none) | Design view: (none) | BOM view: Structured", BomSource.Describe(null, "  "));
        Assert.Equal("Model state: [Primary] | Design view: (none) | BOM view: Structured", BomSource.Describe(" [Primary] ", ""));
    }

    [Fact]
    public void PackageArgs_order() =>
        Assert.Equal(new[] { "package", "--manifest", "m.json", "--out", "o.zip", "--result", "r.json" }, ToolResults.PackageArgs("m.json", "o.zip", "r.json"));

    [Fact]
    public void Counts_reads_integers()
    {
        var c = ToolResults.Counts("{\"ok\":true,\"step\":3,\"pdf\":2,\"zip\":\"x\"}");
        Assert.Equal(3, c["step"]);
        Assert.Equal(2, c["pdf"]);
        Assert.False(c.ContainsKey("zip"));
        Assert.Empty(ToolResults.Counts(null));
        Assert.Empty(ToolResults.Counts("\"nope\""));
    }

    [Fact]
    public void Parse_no_result_uses_public_message() =>
        Assert.Equal(ToolResults.NoResultMessage, ToolResults.Parse(null, "zip", "files").Error);
}
