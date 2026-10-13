using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class ImportRulesTests
{
    private static ImportPlan Plan(string vendor = "McMaster-Carr", string sub = "nuts", string vn = "90510A313", string desc = "Fully Threaded T-Slot Nut") =>
        new(vendor, sub, vn, desc, "Steel", "Black Oxide", "Zak O.");

    [Theory]
    [InlineData(@"C:\Users\z\Downloads\90510A313_Fully Threaded T-Slot Nut.STEP", "McMaster-Carr", "90510A313", "Fully Threaded T-Slot Nut")]
    [InlineData(@"C:\d\91029A104.stp", "McMaster-Carr", "91029A104", "")]
    [InlineData(@"C:\d\SFJ10-100_Linear Shaft.stp", null, "SFJ10-100", "Linear Shaft")]
    [InlineData(@"C:\d\drawing.step", null, "", "drawing")]
    public void Prefill_from_file_names(string step, string? vendor, string vn, string desc)
    {
        var p = VendorFileNames.FromFiles(step, null);
        Assert.Equal(vendor, p.Vendor);
        Assert.Equal(vn, p.VendorNumber);
        Assert.Equal(desc, p.Description);
    }

    [Fact]
    public void Prefill_uses_pdf_name_when_step_name_has_no_description()
    {
        var p = VendorFileNames.FromFiles(@"C:\d\91029A104.stp", @"C:\d\91029A104_Hex Head Screw.pdf");
        Assert.Equal("Hex Head Screw", p.Description);
    }

    [Fact]
    public void Selection_needs_exactly_one_step_and_one_pdf()
    {
        Assert.Null(SelectionCheck.Check(new[] { @"a.STEP", @"a.pdf" }).Error);
        Assert.Equal("a.stp", SelectionCheck.Check(new[] { "a.stp", "a.PDF" }).Step);
        Assert.Contains("PDF", SelectionCheck.Check(new[] { "a.step" }).Error);
        Assert.Contains("STEP", SelectionCheck.Check(new[] { "a.pdf" }).Error);
        Assert.Contains("one", SelectionCheck.Check(new[] { "a.step", "b.stp", "a.pdf" }).Error);
        Assert.NotNull(SelectionCheck.Check(Array.Empty<string>()).Error);
    }

    [Fact]
    public void Validate_names_the_bad_field()
    {
        Assert.Empty(ImportRules.Validate(Plan()));
        Assert.Contains(ImportRules.Validate(Plan(vn: " ")), e => e.Contains("Vendor Number"));
        Assert.Contains(ImportRules.Validate(Plan(desc: "")), e => e.Contains("Description"));
        Assert.Contains(ImportRules.Validate(Plan(vendor: "Bad/Vendor")), e => e.StartsWith("Vendor:"));
        Assert.Contains(ImportRules.Validate(Plan(sub: "a:b")), e => e.Contains("Subfolder"));
        Assert.Contains(ImportRules.Validate(Plan(sub: "nuts.")), e => e.Contains("Subfolder"));
        Assert.Empty(ImportRules.Validate(Plan(sub: "screws/shoulder screws")));
        Assert.Empty(ImportRules.Validate(Plan(sub: "")));
    }

    [Fact]
    public void Paths_and_names()
    {
        Assert.Equal("$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts", ImportRules.VaultFolder(Plan()));
        Assert.Equal("$/DESIGNS/Simplifyber Library/MiSUMi", ImportRules.VaultFolder(Plan(vendor: "MiSUMi", sub: "")));
        Assert.Equal("$/DESIGNS/Simplifyber Library/McMaster-Carr/screws/shoulder screws", ImportRules.VaultFolder(Plan(sub: "screws/shoulder screws")));
        Assert.Equal("CD-001901.ipt", ImportRules.PartFileName("CD-001901"));
        Assert.Equal("CD-001901 McMaster-Carr 90510A313.pdf", ImportRules.PdfFileName("CD-001901", Plan()));
    }

    [Fact]
    public void Vendor_spelling_and_duplicate_key()
    {
        Assert.Equal("McMASTER-CARR", ImportRules.DataCardVendor("McMaster-Carr"));
        Assert.Equal("MiSUMi", ImportRules.DataCardVendor("MiSUMi"));
        Assert.Equal(ImportRules.DuplicateKey("McMaster-Carr", "90510a313 "), ImportRules.DuplicateKey("McMASTER-CARR", "90510A313"));
        Assert.NotEqual(ImportRules.DuplicateKey("MiSUMi", "90510A313"), ImportRules.DuplicateKey("McMaster-Carr", "90510A313"));
    }

    [Fact]
    public void Pdf_name_sanitizes_vendor_number_without_rejecting_it()
    {
        Assert.Empty(ImportRules.Validate(Plan(vn: "SFJ10/100")));
        Assert.Equal("CD-001901 MiSUMi SFJ10-100.pdf", ImportRules.PdfFileName("CD-001901", Plan(vendor: "MiSUMi", vn: "SFJ10/100")));
        Assert.Equal("CD-001901 MiSUMi A-B.pdf", ImportRules.PdfFileName("CD-001901", Plan(vendor: "MiSUMi", vn: "A:B")));
        Assert.Equal("CD-001901 MiSUMi A-B.pdf", ImportRules.PdfFileName("CD-001901", Plan(vendor: "MiSUMi", vn: "A:/B")));
        Assert.Equal("CD-001901 MiSUMi A-B.pdf", ImportRules.PdfFileName("CD-001901", Plan(vendor: "MiSUMi", vn: "A\u0001B")));
    }

    [Fact]
    public void Safe_file_part_trims_edges()
    {
        Assert.Equal("A-B", ImportRules.SafeFilePart(" .A:B. "));
    }

    [Fact]
    public void Control_only_vendor_number_is_rejected()
    {
        Assert.Contains(ImportRules.Validate(Plan(vn: "\u0001\t")), e => e.Contains("Vendor Number"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(" . ")]
    [InlineData("..")]
    [InlineData("///")]
    public void Vendor_number_without_letter_or_digit_is_rejected(string vn)
    {
        Assert.Contains(ImportRules.Validate(Plan(vn: vn)), e => e == "Vendor Number must contain at least one letter or digit.");
    }

    [Fact]
    public void Vendor_number_with_slash_is_still_valid()
    {
        Assert.Empty(ImportRules.Validate(Plan(vn: "SFJ10/100")));
    }

    [Theory]
    [InlineData("MiSUMi", true)]
    [InlineData("Bosch Rexroth", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("trail ", false)]
    [InlineData("end.", false)]
    [InlineData("a*b", false)]
    [InlineData("a\tb", false)]
    public void Name_rule(string name, bool ok) => Assert.Equal(ok, ImportRules.IsValidName(name));

    [Fact]
    public void Validate_refuses_the_vendor_folder_label_as_a_subfolder()
    {
        Assert.Contains(ImportRules.Validate(Plan(sub: "(Vendor Folder)")), e => e.Contains("Subfolder"));
        Assert.Contains(ImportRules.Validate(Plan(sub: "nuts/(vendor folder)")), e => e.Contains("Subfolder"));
    }

    [Fact]
    public void CleanText_strips_control_characters_and_trims()
    {
        Assert.Equal("Hex Screw", ImportRules.CleanText("  Hex\u0001 Screw\t\n "));
        Assert.Equal("", ImportRules.CleanText(null));
    }

    [Theory]
    [InlineData("CD-000123.ipt", true)]
    [InlineData("cd-000123 McMaster-Carr 90510A313.pdf", true)]
    [InlineData("CD-0001234.ipt", false)]
    [InlineData("CD-000124 x.pdf", false)]
    public void Number_clash_covers_own_files_and_pdf_names(string name, bool clash) =>
        Assert.Equal(clash, ImportRules.IsNumberClash(name, "CD-000123"));

    private const string McMasterFolder = "$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts";

    [Fact]
    public void IsDuplicate_vendor_key_match()
    {
        Assert.True(ImportRules.IsDuplicate("McMaster-Carr", "90510 A313", "McMASTER-CARR", "90510a313", "$/Elsewhere"));
        Assert.True(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "mcmaster-carr", "90510A313", null));
    }

    [Fact]
    public void IsDuplicate_blank_file_vendor()
    {
        Assert.True(ImportRules.IsDuplicate("Fastenal", "SFJ10", "", "SFJ10", "$/Elsewhere"));
        Assert.True(ImportRules.IsDuplicate("Fastenal", "SFJ10", "  ", "SFJ10", null));
    }

    [Fact]
    public void IsDuplicate_folder_under_chosen_vendor_even_when_vendor_spelled_differently()
    {
        Assert.True(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "McMaster Carr Supply", "90510A313", McMasterFolder));
        Assert.True(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "Other", "90510A313", "$/DESIGNS/Simplifyber Library/McMaster-Carr"));
    }

    [Fact]
    public void IsDuplicate_is_false_for_same_number_other_vendor_and_other_folder()
    {
        Assert.False(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "Festo", "90510A313", "$/DESIGNS/Simplifyber Library/Festo"));
        Assert.False(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "Festo", "90510A313", "$/DESIGNS/Simplifyber Library/McMaster-Carrot"));
        Assert.False(ImportRules.IsDuplicate("McMaster-Carr", "90510A313", "", "90510A314", McMasterFolder));
        Assert.False(ImportRules.IsDuplicate("McMaster-Carr", "", "", "", McMasterFolder));
    }

    [Fact]
    public void Material_is_required()
    {
        var p = Plan() with { Material = "  " };
        Assert.Contains("Material is required.", ImportRules.Validate(p));
        Assert.Empty(ImportRules.Validate(Plan()));
    }

    [Theory]
    [InlineData(null, "As Finished")]
    [InlineData("", "As Finished")]
    [InlineData("   ", "As Finished")]
    [InlineData(" Black Oxide ", "Black Oxide")]
    public void Empty_finish_becomes_as_finished(string? finish, string expected) =>
        Assert.Equal(expected, ImportRules.NormalizeFinish(finish));

    [Fact]
    public void Preferred_material_is_steel_else_first()
    {
        Assert.Equal("steel", ImportRules.PreferredMaterial(new[] { "Aluminum 6061", "steel", "Brass" }));
        Assert.Equal("Aluminum 6061", ImportRules.PreferredMaterial(new[] { "Aluminum 6061", "Brass" }));
        Assert.Equal("Steel", ImportRules.PreferredMaterial(new[] { "Aluminum 6061", "Steel, Mild", "Steel" }));
        Assert.Equal("Steel, Mild", ImportRules.PreferredMaterial(new[] { "Aluminum 6061", "Steel, Mild", "Stainless Steel" }));
        Assert.Equal("steel, mild", ImportRules.PreferredMaterial(new[] { "Aluminum 6061", "steel, mild" }));
        Assert.Null(ImportRules.PreferredMaterial(Array.Empty<string>()));
    }

    [Fact]
    public void Empty_card_properties_names_the_blank_ones()
    {
        var values = new Dictionary<string, string?> { ["Source"] = "Buy", ["Vendor"] = " ", ["Material"] = null };
        Assert.Equal(new[] { "Vendor", "Vendor Number", "Material", "Material Finish" },
            ImportRules.EmptyCardProperties(n => values.GetValueOrDefault(n)));
    }

    [Theory]
    [InlineData("Zak Olech", "Zak O.")]
    [InlineData("  zak   olech ", "zak O.")]
    [InlineData("Mary Ann Smith", "Mary S.")]
    [InlineData("Zak O.", "Zak O.")]
    [InlineData("Zak", "Zak")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Designer_name_is_first_name_and_last_initial(string? input, string expected) =>
        Assert.Equal(expected, ImportRules.DesignerName(input));

    [Theory]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\screws\CD-001907.ipt", true)]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\screws\cd-001907.IPT", true)]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\screws\CD-001906.ipt", false)]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\nuts\CD-001907.ipt", false)]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\screws\Part1.ipt", false)]
    [InlineData(@"C:\W\screws\CD-001906.ipt", @"C:\W\screws\CD-001907.stp", false)]
    public void Vault_renumbered_save_is_a_cd_ipt_in_the_same_folder(string requested, string saved, bool expected) =>
        Assert.Equal(expected, ImportRules.IsVaultRenumberedSave(requested, saved));
}
