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
        Assert.Contains(ImportRules.Validate(Plan(vendor: "Bad/Vendor")), e => e.Contains("Vendor"));
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

    [Theory]
    [InlineData("MiSUMi", true)]
    [InlineData("Bosch Rexroth", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("trail ", false)]
    [InlineData("end.", false)]
    [InlineData("a*b", false)]
    public void Name_rule(string name, bool ok) => Assert.Equal(ok, ImportRules.IsValidName(name));
}
