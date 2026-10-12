# Import New Part (ribbon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Import New Part button that files a vendor STEP + PDF into Vault as a numbered Part - Purchased .ipt under `$/DESIGNS/Simplifyber Library/<Vendor>/<Subfolder>`, data card filled, PDF attached.

**Architecture:** Pure rules (file-name prefill, plan validation, paths, names, vendor spelling, duplicate key) in `SFVaultBridge.Core` with xUnit tests. The add-in adds Vault lookups/writes to `VaultFiles`, a material option to `StepImporter`, an `ImportPartRunner` that executes the steps in order, and a WinForms `ImportPartForm` behind a fourth ribbon button.

**Tech Stack:** C# .NET 8, WinForms, Inventor 2026 interop, Vault 2026 SDK, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-12-ribbon-import-vendor-part-design.md`

## Global Constraints

- Library root `$/DESIGNS/Simplifyber Library`; part `<CD>.ipt`, PDF `<CD> <Vendor> <Vendor Number>.pdf`; CD numbers `^CD-\d{6}$` from the default file numbering scheme, clash-checked by name.
- Vendor spelling on the data card: folder `McMaster-Carr` is written as `McMASTER-CARR`; every other vendor as its folder name.
- Duplicate = same Vendor (after the spelling map) and same Vendor Number, compared case-insensitively with all whitespace removed. A duplicate stops the import before any write.
- iProperties exactly as the spec's Data card mapping table; Source `Buy`; Engr Approved By `NOT REVIEWED`; Designer = Inventor General Options user name.
- Steps in order: duplicate, number, folders, part (StepImporter), PDF (Documents), add part with PDF attached (Part - Purchased), read back, tidy downloads to `Downloads\Imported\`. On failure: stop, name the step, list what reached Vault, keep downloads.
- Nothing reaches Inventor; dialogs owned by Inventor's main window; a click while a run is going shows "Already running." No em dashes in user-facing strings.
- Never run the installer, start/automate Inventor, or connect to Vault while building; gates are Release build 0 errors + `dotnet test inventor-addin/SFVaultBridge.sln` + `py -3.14 -m pytest -q` unchanged.

## Review Focus

1. **Vendor or subfolder name with characters Vault/Windows reject** (`/ \ : * ? " < > |`, trailing dot or space, empty): the form refuses it with the field named. Test in Task 1.
2. **File names that do not follow McMaster's pattern** (`MISUMI_SFJ10-100.stp`, `drawing.pdf`, no underscore): prefill falls back gracefully (empty or best guess), never throws. Test in Task 1.
3. **Same part already in Vault under a differently spaced/cased vendor number** (`90510a313 ` vs `90510A313`): caught as duplicate. Test in Task 1 (key) and Task 2 (search uses the key).
4. **STEP and PDF selections that are not exactly one of each:** clear message, nothing imported. Test in Task 1 (`SelectionCheck`).
5. **A failure after the PDF reached Vault but before the part:** message lists the PDF as already in Vault. Covered by Task 2's step reporting (live-checked).

---

### Task 1: Core import rules

**Files:**
- Create: `inventor-addin/SFVaultBridge.Core/ImportRules.cs`
- Test: `inventor-addin/SFVaultBridge.Core.Tests/ImportRulesTests.cs`

**Interfaces (namespace SFVaultBridge.Core):**
- `record Prefill(string? Vendor, string VendorNumber, string Description)`
- `static class VendorFileNames`: `Prefill FromFiles(string stepPath, string? pdfPath)`
- `record SelectionResult(string? Step, string? Pdf, string? Error)`; `static class SelectionCheck`: `SelectionResult Check(IEnumerable<string> paths)`
- `record ImportPlan(string Vendor, string Subfolder, string VendorNumber, string Description, string Material, string MaterialFinish, string Designer)`
- `static class ImportRules`:
  - `const string LibraryRoot = "$/DESIGNS/Simplifyber Library"`
  - `IReadOnlyList<string> Validate(ImportPlan plan)` (empty = valid; messages name the field)
  - `string VaultFolder(ImportPlan plan)` (`LibraryRoot/Vendor[/Subfolder]`, Subfolder may contain `/` for nesting; empty Subfolder = vendor folder)
  - `string DataCardVendor(string vendorFolder)` (McMaster-Carr -> McMASTER-CARR)
  - `string PartFileName(string cd)` (`CD-001901.ipt`), `string PdfFileName(string cd, ImportPlan plan)` (`CD-001901 McMaster-Carr 90510A313.pdf`, folder spelling)
  - `string DuplicateKey(string vendor, string vendorNumber)` (DataCardVendor applied, upper-case, whitespace removed, joined with `|`)
  - `bool IsValidName(string name)` (folder/vendor name rule)

- [ ] **Step 1: Write the failing tests** (`ImportRulesTests.cs`)

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build FAIL, `VendorFileNames` not found

- [ ] **Step 3: Implement `ImportRules.cs`**

```csharp
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core;

public sealed record Prefill(string? Vendor, string VendorNumber, string Description);

/// <summary>Best-effort form prefill from vendor file names. Never throws.</summary>
public static class VendorFileNames
{
    private static readonly Regex McMaster = new(@"^(?<pn>\d{3,5}[A-Z]\d{1,4})(?:_(?<desc>.+))?$", RegexOptions.IgnoreCase);

    public static Prefill FromFiles(string stepPath, string? pdfPath)
    {
        var step = Parse(Path.GetFileNameWithoutExtension(stepPath ?? ""));
        if (pdfPath is null || step.Description.Length > 0) return step;
        var pdf = Parse(Path.GetFileNameWithoutExtension(pdfPath));
        return step with { Description = pdf.Description, VendorNumber = step.VendorNumber.Length > 0 ? step.VendorNumber : pdf.VendorNumber };
    }

    private static Prefill Parse(string stem)
    {
        stem = (stem ?? "").Trim();
        var m = McMaster.Match(stem);
        if (m.Success) return new Prefill("McMaster-Carr", m.Groups["pn"].Value.ToUpperInvariant(), m.Groups["desc"].Value.Trim());
        var i = stem.IndexOf('_');
        return i > 0 ? new Prefill(null, stem[..i].Trim(), stem[(i + 1)..].Trim()) : new Prefill(null, "", stem);
    }
}

public sealed record SelectionResult(string? Step, string? Pdf, string? Error);

public static class SelectionCheck
{
    public static SelectionResult Check(IEnumerable<string> paths)
    {
        var list = (paths ?? Array.Empty<string>()).ToList();
        var steps = list.Where(p => p.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".step", StringComparison.OrdinalIgnoreCase)).ToList();
        var pdfs = list.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (steps.Count == 1 && pdfs.Count == 1) return new SelectionResult(steps[0], pdfs[0], null);
        if (steps.Count > 1 || pdfs.Count > 1) return new SelectionResult(null, null, "Select exactly one STEP file and one PDF.");
        if (steps.Count == 0 && pdfs.Count == 0) return new SelectionResult(null, null, "Select the vendor's STEP file and its PDF.");
        return new SelectionResult(null, null, steps.Count == 0 ? "Also select the STEP file." : "Also select the PDF.");
    }
}

public sealed record ImportPlan(string Vendor, string Subfolder, string VendorNumber, string Description, string Material, string MaterialFinish, string Designer);

public static class ImportRules
{
    public const string LibraryRoot = "$/DESIGNS/Simplifyber Library";
    private static readonly char[] Bad = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.EndsWith('.') && name.IndexOfAny(Bad) < 0;

    public static IReadOnlyList<string> Validate(ImportPlan p)
    {
        var errors = new List<string>();
        if (!IsValidName(p.Vendor ?? "")) errors.Add("Vendor: choose a vendor, or a new name without / \\ : * ? \" < > | and no trailing dot or space.");
        var sub = p.Subfolder ?? "";
        if (sub.Length > 0 && sub.Split('/').Any(s => !IsValidName(s))) errors.Add("Subfolder: each level needs a name without \\ : * ? \" < > | and no trailing dot or space.");
        if (string.IsNullOrWhiteSpace(p.VendorNumber)) errors.Add("Vendor Number is required.");
        if (string.IsNullOrWhiteSpace(p.Description)) errors.Add("Description is required.");
        return errors;
    }

    public static string VaultFolder(ImportPlan p) =>
        string.IsNullOrEmpty(p.Subfolder) ? $"{LibraryRoot}/{p.Vendor}" : $"{LibraryRoot}/{p.Vendor}/{p.Subfolder.Trim('/')}";

    public static string DataCardVendor(string vendorFolder) =>
        string.Equals(vendorFolder, "McMaster-Carr", StringComparison.OrdinalIgnoreCase) ? "McMASTER-CARR" : vendorFolder;

    public static string PartFileName(string cd) => cd + ".ipt";

    public static string PdfFileName(string cd, ImportPlan p) => $"{cd} {p.Vendor} {p.VendorNumber.Trim()}.pdf";

    public static string DuplicateKey(string vendor, string vendorNumber) =>
        Regex.Replace(DataCardVendor((vendor ?? "").Trim()), @"\s+", "").ToUpperInvariant() + "|" +
        Regex.Replace(vendorNumber ?? "", @"\s+", "").ToUpperInvariant();
}
```

- [ ] **Step 4: Run tests to pass**, then **Step 5: Commit** (`git add inventor-addin/SFVaultBridge.Core inventor-addin/SFVaultBridge.Core.Tests`; message `Import rules: prefill, selection, validation, paths, vendor spelling, duplicate key`).

---

### Task 2: Add-in import runner and Vault/Inventor support

**Files:**
- Modify: `inventor-addin/SFVaultBridge.Addin/VaultFiles.cs`, `StepImporter.cs`
- Create: `inventor-addin/SFVaultBridge.Addin/ImportPartRunner.cs`

**Interfaces:**
- `VaultFiles.LibraryVendors() -> IReadOnlyList<string>` (immediate subfolder names of LibraryRoot, sorted)
- `VaultFiles.Subfolders(string vendor) -> IReadOnlyList<string>` (relative paths below the vendor folder, nested joined with `/`, sorted, max depth 3)
- `VaultFiles.FindByVendorNumber(string vendor, string vendorNumber) -> WsFile?` (search Vault files whose "Vendor Number" property equals the number (search condition on the property, latest only, all folders), then keep the first whose `ImportRules.DuplicateKey(<its Vendor property>, <its Vendor Number>)` equals the plan's key)
- `VaultFiles.NextPartNumber() -> string` (default FILE numbering scheme, `GenerateFileNumber(schemeId, empty)`; throw BridgeException("number", ...) if it does not match `^CD-\d{6}$` or any latest file named `<CD>.<anything>` exists (use the existing name search))
- `VaultFiles.EnsureFolder(string vaultPath) -> void` (create each missing level under LibraryRoot, inheriting IsLib from the parent)
- `VaultFiles.AddNew(string localPath, string vaultFolder, string fileName, long[] attachmentFileIds, string category, string comment) -> WsFile` (AddFile + category; used for PDF then part)
- `StepImporter.ImportStepAsPart(stepPath, outIpt, design, custom, string? material)` overload: after iProperties, when `material` is non-empty, find it in `app.ActiveMaterialLibrary.MaterialAssets` (by DisplayName), copy into the document if needed, and set `PartComponentDefinition.Material`/`ActiveMaterial` (confirm the 2026 member); a missing material becomes a warning, not an error.
- `ImportPartRunner.Run(Inv.Application app, ImportPlan plan, string stepPath, string pdfPath) -> ImportResult` where `record ImportResult(bool Ok, string? Cd, string? VaultPath, string? FailedStep, string? Error, IReadOnlyList<string> AlreadyInVault, IReadOnlyList<string> Warnings, string? LocalIpt)`; executes the spec's steps 1-8 in order, catching per step, never throwing.

Inventor/Vault members were not compiled while writing this plan: confirm each against `C:\Program Files\Autodesk\Inventor 2026\Bin\Autodesk.Inventor.Interop.dll` and `C:\ProgramData\Autodesk\ApplicationPlugins\VaultInventor2026.bundle\Contents` by compiling/reflecting; adapt and list every adaptation. Reuse existing helpers (`VaultSession.Connection`, `VaultFiles.FindLatest`, the name search added in round 2, `DrawingExporter`-style SilentOperation handling is already in StepImporter).

Local path for the part: `Path.Combine(VaultFiles.WorkingFolderRoot(), vaultFolder without "$/" with "/"->"\")` + file name; create the directory.

iProperties (design): Part Number=cd, Description, Vendor=`ImportRules.DataCardVendor(plan.Vendor)`, Catalog Web Link=VendorNumber, Designer=plan.Designer. (custom): Vendor Number, Source=Buy, Material Finish (when non-empty), Engr Approved By=NOT REVIEWED.

Tidy: move step and pdf into `%USERPROFILE%\Downloads\Imported\` (create; add " (n)" on name clash); a move failure is a warning.

- [ ] Steps: implement, build (`dotnet build inventor-addin/SFVaultBridge.sln -c Release`, 0 errors), run tests (green), commit `Import New Part: runner, Vault lookups and writes, material`.

---

### Task 3: Form, button, icon, README

**Files:**
- Create: `inventor-addin/SFVaultBridge.Addin/ImportPartForm.cs`
- Modify: `RibbonActions.cs` (fourth large button **Import New Part**, works without an open document), `Icons.cs` (icon: a box with a down arrow, same flat style, 16/32 px, transparent), `inventor-addin/README.md`

- Click: `OpenFileDialog` (InitialDirectory Downloads, Filter `Vendor files (*.stp;*.step;*.pdf)|*.stp;*.step;*.pdf`, Multiselect) -> `SelectionCheck.Check`; on Error show it and reopen the dialog until cancel or valid.
- Form (owned by Inventor's main window, `FormBorderStyle.FixedDialog`, Tab order top to bottom): Vendor ComboBox (DropDownList with LibraryVendors + "New vendor..." which prompts for a name via a small input dialog and validates with `ImportRules.IsValidName`), Subfolder ComboBox (refreshes on vendor change: "(vendor folder)", Subfolders, "New subfolder..."), Vendor Number, Description, Material ComboBox (DropDown, items = active material library display names, may be empty), Material Finish, Designer (read-only, `app.GeneralOptions.UserName`), Engr Approved By (read-only `NOT REVIEWED`), a live "Will create" label (`VaultFolder/CD-<next>.ipt` and the PDF name with `CD-<next>`), Import and Cancel buttons. Import runs `ImportRules.Validate`; errors shown inline in a label; on valid, closes and the runner runs with the wait cursor.
- Prefill from `VendorFileNames.FromFiles`; Vendor preselect = prefill vendor if present in the list, else the last vendor used this session.
- Result message (owned): success -> "CD-xxxxxx added to <path>. PDF attached." + warnings, Yes/No "Open the part now?" (opens via `app.Documents.Open(localIpt, true)`); failure -> failed step, error, and "Already in Vault: ..." when non-empty.
- README: Import New Part section (steps, where files go, data card fields, duplicate rule, what to do on failure).
- [ ] Steps: implement, build 0 errors, tests green, commit `Import New Part: form, ribbon button, icon, README`.
