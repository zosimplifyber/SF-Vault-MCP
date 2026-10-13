# Manufacturing Packages and Process Property (ribbon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every add-in BOM shows a State column with unreleased rows in light yellow, and two new ribbon buttons build zipped manufacturing packages: Partner Package (All Make parts / CNC / Sheet metal: STEP, PDF drawings, DXF flat patterns, BOM.xlsx, cover) and 3D Print Package (3MF or STL per part, Print List.xlsx), with parts chosen by a `Process` iProperty or fallback rules.

**Architecture:** Core (pure, tested) owns classification, inclusion, file and zip names, print-material matching, the settings file and the package manifest JSON. The add-in reads the BOM (now with Process, Material Finish and a sheet-metal flag), exports STEP/DXF/3MF/STL/PDF into a temp folder and writes a manifest. A new `SFVaultTools.exe package` subcommand (Python, `manufacturing_package.py`) stamps unreleased PDFs, builds the xlsx and cover, and zips. The add-in then copies the zip to Downloads and checks it into Vault next to the assembly.

**Tech Stack:** C# .NET 8 (Core + Inventor 2026 add-in), xUnit, WinForms; Python 3.14 (openpyxl, pandas, pypdf, reportlab, zipfile); PyInstaller one-file exe; Inventor 2026 translators (STEP, PDF, 3MF, STL); Vault 2026 SDK.

**Spec:** `docs/superpowers/specs/2026-10-13-ribbon-manufacturing-packages-design.md`

## Global Constraints

- No em dashes anywhere (code, comments, strings, README, commit messages). Use a colon, comma or plain hyphen.
- Every commit message ends with a blank line, then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never commit `bin/`, `obj/`, `inventor-addin/tools/`, `build/` or `config.json`. Stage files by explicit path, never `git add -A` or `git add .`.
- Never run `Install-VaultBridge.ps1`, never start or automate Inventor, never connect to Vault while building or testing. Add-in code is build-verified only; the live run on CD-001882 is done with Zak afterwards.
- Existing outputs stay unchanged except: the State column (Design Review and Assembly Package BOM tables) and the yellow `#FFF59D` fill on unreleased rows (those tables and the Purchasing tab). All existing tests stay green; the only existing test that may be edited is the BomFile header test (Task 1), because the spec adds a `Process` column.
- Released means `state.strip().lower() == "released"` (C#: `Trim()` + `OrdinalIgnoreCase`). Blank state is not released.
- Yellow fill value: `FFF59D` (openpyxl `PatternFill("solid", fgColor="FFF59D")`; reportlab `colors.HexColor("#FFF59D")`).
- Process values (settings defaults, editable): `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other`. Matching is trimmed and case-insensitive.
- Print materials (settings defaults, editable): `PLA`, `PETG`, `ABS`, `ASA`, `PA11`, `PA12`, `Nylon`, `TPU`, `PC`, `Resin`, `CF`, each matched as a whole token (not preceded or followed by a letter or digit), case-insensitive.
- Settings file: `%APPDATA%\Simplifyber\VaultBridge\settings.json`, keys `processValues`, `printMaterials`, `recentPartners`; recent partners: last 10, most recent first, case-insensitive dedupe; malformed file: log, use defaults, never overwrite.
- File names: `<CD> Rev <rev>.stp|.pdf|.dxf`, print `<CD> Rev <rev> x<qty>.3mf|.stl` in a folder per material; `<CD>` is the model file stem, rev `-` when empty. Zips: `<assembly> <Partner> <Mode> Package.zip` (Mode `Make`, `CNC`, `Sheet Metal`) and `<assembly> 3D Print Package.zip`.
- STL translator ClassId `{533E9A98-FC3B-11D4-8E7E-0010B541CD80}`. The 3MF translator is found by enumerating `ApplicationAddIns` for a `TranslatorAddIn` whose `DisplayName` contains `3MF`; never hard-code its id.
- DXF: `WriteDataToFile("FLAT PATTERN DXF?AcadVersion=2018&OuterProfileLayer=Outer", path)` on the sheet metal component definition's `DataIO`. Never create a flat pattern that does not exist.
- Gates for every task (run from the worktree root in Git Bash): `export PATH="/c/Program Files/dotnet:$PATH"`; `dotnet build inventor-addin/SFVaultBridge.sln -c Release` with 0 errors; `dotnet test inventor-addin/SFVaultBridge.sln` green (164+ before this plan, more after); `py -3.14 -m pytest -q` green (870+ before this plan, more after).

### Package manifest contract (Core writes it in Task 1, Python reads it in Task 3)

```json
{
  "kind": "partner",
  "assembly": "CD-001882",
  "description": "Big blue adapter",
  "revision": "A",
  "state": "Released",
  "partner": "Acme Machining",
  "mode": "CNC",
  "source": "Model state: Primary | Design view: Default | BOM view: Structured",
  "user": "zak",
  "created": "2026-10-13 14:05",
  "rows": [
    {"item": "1.2", "part_number": "CD-000123", "description": "Plate", "qty": 4, "material": "Aluminum 6061",
     "finish": "As Finished", "process": "guessed: CNC", "revision": "A", "state": "Work in Progress",
     "files": ["STEP/CD-000123 Rev A.stp", "PDF/CD-000123 Rev A.pdf"]}
  ],
  "files": [{"src": "C:\\Users\\x\\AppData\\Local\\Temp\\SFVaultBridge\\abc\\export\\001.stp", "dest": "STEP/CD-000123 Rev A.stp", "stamp": false}],
  "guessed": ["CD-000123 (CNC)"],
  "missing_drawings": ["CD-000124"],
  "no_flat_pattern": ["CD-000125"],
  "failed": [{"part": "CD-000126", "reason": "STEP export failed: ..."}]
}
```

- `kind` is `partner` or `print`. `mode` is `Make`, `CNC`, `Sheet Metal` or `3D Print`. `partner` is `""` for print. Every list may be empty; Python treats any missing key as empty/blank.
- `dest` is relative with `/` separators; Python rejects absolute paths, drive letters and `..` segments (fail closed: `ok: false`).
- `stamp: true` (only on `.pdf` dests) means stamp NOT RELEASED on every page.

## Review Focus

1. The same part used in several sub-assemblies (and at different quantities) appears once in the package with Qty = total in the assembly (row qty times every parent qty, summed over occurrences). Test in Task 1 (`Select_dedupes_and_totals_quantities`).
2. A purchased sub-assembly's children never land in a package (they are bought with it), while a phantom's children still do. Test in Task 1 (`Select_prunes_children_of_buy_and_reference_rows_not_phantom`).
3. A mode with nothing to include (for example Sheet metal on an all-machined assembly) shows `No sheet metal parts found.` in the form with Build disabled, and writes nothing. Test in Task 1 (`PreviewText_for_empty_selection`) plus form wiring in Task 5.
4. A partner name with illegal characters, a trailing dot or only spaces is refused by the form with a message, and a zip name never contains illegal characters even if one slips through. Tests in Task 1 (`PartnerError_*`, `PartnerZipName_sanitizes`).
5. Materials such as `Generic Plastic`, `PCB laminate`, `Polycarbonate` or `Carbon Fiber Sheet` are not print materials, while `PC-ABS`, `PLA+` and `Onyx (PA12-CF)` are. Test in Task 1 (`PrintMaterials_word_boundaries`).

---

### Task 1: Core: Process on BomNode/BomFile, PackageRules, PrintMaterials, BridgeSettings, PackageManifest, tool args

**Files:**
- Modify: `inventor-addin/SFVaultBridge.Core/Bom.cs` (BomNode trailing fields; BomFile `Process` column)
- Create: `inventor-addin/SFVaultBridge.Core/Packages.cs` (enums, records, `PackageRules`, `PrintMaterials`, `PackageManifest`)
- Create: `inventor-addin/SFVaultBridge.Core/BridgeSettings.cs`
- Modify: `inventor-addin/SFVaultBridge.Core/Outputs.cs` (`ToolResults.PackageArgs`, `ToolResults.Counts`, public `NoResultMessage`)
- Modify: `inventor-addin/SFVaultBridge.Core.Tests/BomTests.cs` (header test gains `Process`; new Process test)
- Create: `inventor-addin/SFVaultBridge.Core.Tests/PackageRulesTests.cs`, `inventor-addin/SFVaultBridge.Core.Tests/BridgeSettingsTests.cs`
- Modify: `inventor-addin/SFVaultBridge.Core.Tests/OutputsTests.cs` (args, counts)

**Interfaces:**
- Consumes: `ReviewRules.Classify(BomNode, string?) -> PartKind`, `SourceRules.IsBuyCategory(string?)`, `ImportRules.IsValidName`, `ImportRules.SafeFilePart`, `BomOrdering.Flatten`.
- Produces (all in namespace `SFVaultBridge.Core`):

```csharp
public sealed record BomNode(string PartNumber, string Description, double Quantity, string UnitQuantity, string BomStructure,
                             string Revision, string Material, string FileName, string FullPath, IReadOnlyList<BomNode> Children,
                             string Vendor = "", string VendorNumber = "", string State = "",
                             string Process = "", string MaterialFinish = "", bool IsSheetMetal = false);
// BomFile.Headers: Item, Part Number, BOM Structure, Unit QTY, QTY, Description, REV, State, Material, Process, File Name, Vendor, Vendor Number

public enum PackageKind { None, Cnc, SheetMetal, Print, OtherMake }
public enum ManufacturingMode { AllMake, Cnc, SheetMetal, Print }
[Flags] public enum PackageFiles { None = 0, Step = 1, Pdf = 2, Dxf = 4, Print = 8 }
public readonly record struct PackageClass(PackageKind Kind, bool Guessed);
public sealed record PackagePart(string Item, BomNode Node, PackageClass Class, PackageFiles Files, double TotalQty);

public sealed class PrintMaterials
{
    public PrintMaterials(IEnumerable<string> entries);
    public bool Matches(string? material);
}

public static class PackageRules
{
    public const string TopItem = "Top";
    public static bool IsReleased(string? state);
    public static PackageClass Classify(BomNode node, string? vaultCategory, PrintMaterials materials, bool isSheetMetal);
    public static PackageFiles FilesFor(ManufacturingMode mode, PackageKind kind);
    public static IReadOnlyList<PackagePart> Select(IReadOnlyList<BomNode> roots, ManufacturingMode mode, PrintMaterials materials,
                                                    Func<BomNode, string?> categoryOf, BomNode? top = null);
    public static string KindLabel(PackageKind kind);          // "CNC", "Sheet Metal", "3D Print", "Other make", "" for None
    public static string ModeName(ManufacturingMode mode);     // "Make", "CNC", "Sheet Metal", "3D Print"
    public static string ProcessText(PackagePart part);        // trimmed Process, or "guessed: <KindLabel>"
    public static string GuessedEntry(PackagePart part);       // "<PartNumber> (<KindLabel>)"
    public static string QtyText(double qty);                  // "0.###" invariant: 4, 2.5
    public static string FileStem(BomNode node);               // "<SafeFilePart(file stem)> Rev <SafeFilePart(rev) or ->"
    public static string Dest(PackageFiles file, PackagePart part);   // Step/Pdf/Dxf only: "STEP/<stem>.stp", "PDF/<stem>.pdf", "DXF/<stem>.dxf"
    public static string PrintDest(PackagePart part, string extension); // "<MaterialFolder>/<stem> x<QtyText>.<ext without dot>"
    public static string MaterialFolder(string? material);     // SafeFilePart(material), or "No material" when that is empty
    public static string? PartnerError(string? partner);       // null when valid
    public static string PartnerZipName(string assemblyStem, string partner, ManufacturingMode mode);
    public static string PrintZipName(string assemblyStem);
    public static string PreviewText(IReadOnlyList<PackagePart> parts, ManufacturingMode mode);
}

public sealed record PackageRow(string Item, string PartNumber, string Description, double Qty, string Material, string Finish,
                                string Process, string Revision, string State, IReadOnlyList<string> Files);
public sealed record PackageFile(string Src, string Dest, bool Stamp);
public sealed record PackageFailure(string Part, string Reason);
public sealed record PackageManifestData(string Kind, string Assembly, string Description, string Revision, string State,
                                         string Partner, string Mode, string Source, string User, DateTime Created,
                                         IReadOnlyList<PackageRow> Rows, IReadOnlyList<PackageFile> Files,
                                         IReadOnlyList<string> Guessed, IReadOnlyList<string> MissingDrawings,
                                         IReadOnlyList<string> NoFlatPattern, IReadOnlyList<PackageFailure> Failed);
public static class PackageManifest { public static string ToJson(PackageManifestData m); }

public sealed record BridgeSettings(IReadOnlyList<string> ProcessValues, IReadOnlyList<string> PrintMaterials, IReadOnlyList<string> RecentPartners)
{
    public const int MaxRecent = 10;
    public static BridgeSettings Defaults { get; }
    public static string DefaultPath { get; }   // Environment.SpecialFolder.ApplicationData + "Simplifyber\VaultBridge\settings.json"
    public static BridgeSettings Load(string path, Action<string> log);
    public static IReadOnlyList<string> WithRecent(IReadOnlyList<string> recent, string partner);
    public static bool RememberPartner(string path, string partner, Action<string> log);
}

// Outputs.cs, ToolResults
public const string NoResultMessage = "SFVaultTools gave no usable result. See bridge.log."; // replaces the private NoResult, same text
public static IReadOnlyList<string> PackageArgs(string manifest, string outZip, string resultPath); // package --manifest M --out Z --result R
public static IReadOnlyDictionary<string, int> Counts(string? json); // every top-level integer field; empty on bad JSON
```

Rules the implementation must follow exactly:

- `Classify`: (1) `FullPath` or `FileName` blank (virtual component) -> `(None, false)`. (2) `ReviewRules.Classify(node, vaultCategory) != PartKind.Make` -> `(None, false)` (covers Phantom, Reference, Purchased structure, Part - Purchased, Part - Content Center, `\Content Center Files\` paths). (3) Process set: `CNC` -> Cnc, `Sheet Metal` -> SheetMetal, `3D Print` -> Print, anything else -> OtherMake; `Guessed = false`. (4) Process blank, `Guessed = true`: `isSheetMetal` -> SheetMetal; else `materials.Matches(node.Material)` -> Print; else `.ipt` -> Cnc; else OtherMake.
- `FilesFor`: None -> None for every mode. AllMake: `Step | Pdf`, plus `Dxf` when kind is SheetMetal. Cnc: kind Cnc -> `Step | Pdf`. SheetMetal: kind SheetMetal -> `Dxf | Pdf`. Print: kind Print -> `Print`. Everything else -> None.
- `Select`: walk `BomOrdering.Flatten(roots)` (preorder with `Depth`), keeping per depth the cumulative multiplier and a "pruned" flag. A line's multiplier is its `Quantity` times its parent's multiplier (1 at depth 0). A line is skipped when any ancestor pruned; a line prunes its children when its effective structure is `Purchased` or `Reference` (case-insensitive) or `SourceRules.IsBuyCategory(categoryOf(node))`. Phantom does not prune. Classify each unskipped line with `categoryOf(node)` and `node.IsSheetMetal`. Dedupe by `FullPath` (OrdinalIgnoreCase): the first line keeps its `Item` and node, `TotalQty` sums the multipliers of every unskipped occurrence. Keep parts whose `FilesFor(mode, kind) != None`, in first-occurrence order. When `top` is given and mode is AllMake, prepend `new PackagePart(TopItem, top, Classify-by-Process-only, PackageFiles.Step | PackageFiles.Pdf, 1)` where the class is from step (3)/(4) above with `isSheetMetal: false` (an `.iam` with blank Process is `(OtherMake, true)`).
- `PrintMaterials.Matches`: for each non-blank trimmed entry, regex `(?<![A-Za-z0-9])` + `Regex.Escape(entry)` + `(?![A-Za-z0-9])`, `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`; null/blank material -> false.
- `PartnerError`: trimmed empty -> `"Partner is required."`; `!ImportRules.IsValidName(trimmed)` -> `"Partner: use a name without / \\ : * ? \" < > | and no trailing dot."`; length > 60 -> `"Partner: 60 characters at most."`; else null.
- `PartnerZipName`: `$"{stem} {ImportRules.SafeFilePart(partner.Trim())} {ModeName(mode)} Package.zip"`. `PrintZipName`: `$"{stem} 3D Print Package.zip"`.
- `PreviewText`: zero parts -> `"No Make parts found."` / `"No CNC parts found."` / `"No sheet metal parts found."` / `"No 3D print parts found."`; otherwise `"<n> parts: <s> STEP, <p> PDF, <d> DXF"` (counts of parts whose `Files` has that flag; PDF counts planned drawings, a missing drawing is reported later on the cover); for Print mode `"<n> parts: <n> print files"`. Singular `"1 part: ..."` and `"1 print file"` when the count is 1.
- `BridgeSettings.Load`: file missing -> try to create its folder and write `Defaults` (indented JSON, keys in the order `processValues`, `printMaterials`, `recentPartners`); a write failure is logged, never thrown; return `Defaults`. File present but `JsonException` or root not an object -> log `settings.json is not valid JSON (<message>); using defaults.`, return `Defaults`, file untouched. Each key that is missing or not an array -> that key's default; non-string and blank entries are skipped; entries are trimmed. `recentPartners` is passed through `WithRecent` normalization (dedupe, cap).
- `WithRecent`: trimmed partner first, then the old list minus any case-insensitive match, capped at `MaxRecent`; a blank partner returns the list unchanged.
- `RememberPartner`: re-reads the file. Malformed -> log, return false, file untouched. Missing -> write defaults with `recentPartners = [partner]`. Otherwise parse to `JsonObject`, set only `recentPartners` (other keys, including unknown ones, preserved), write indented UTF-8 without BOM, return true. IO failures are logged and return false.
- `PackageManifest.ToJson` writes exactly the contract keys above; `created` as `yyyy-MM-dd HH:mm` invariant; `qty` as a JSON number.

- [ ] **Step 1: Write the failing tests**

`BomTests.cs`: change the header assertion in `Writes_headers_and_escapes_fields` to
`"Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tState\tMaterial\tProcess\tFile Name\tVendor\tVendor Number\r\n"` and the row assertion to
`"1\tCD-1\tPurchased\tEach\t2.5\t\"1/2\"\" bolt with tab\"\t\t\tSteel\t\tCD-1.ipt\t\t\r\n"`. Add:

```csharp
[Fact]
public void Writes_process_after_material()
{
    var path = Path.Combine(_dir, "bom-process.txt");
    var node = new BomNode("CD-4", "plate", 1, "Each", "Normal", "A", "Aluminum", "CD-4.ipt", @"C:\x\CD-4.ipt", Array.Empty<BomNode>(), Process: " CNC ");
    BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
    Assert.Contains("\tAluminum\tCNC\tCD-4.ipt\t", File.ReadAllText(path));
}
```

`PackageRulesTests.cs` (helper and cases):

```csharp
private static readonly PrintMaterials Mats = new(BridgeSettings.Defaults.PrintMaterials);

private static BomNode N(string file, double qty = 1, string structure = "Normal", string process = "", string material = "Steel",
                         bool sheet = false, string rev = "A", string state = "Released", params BomNode[] kids) =>
    new(Path.GetFileNameWithoutExtension(file), "d " + file, qty, "Each", structure, rev, material, file,
        @"C:\Vault Workspace\DESIGNS\" + file, kids, State: state, Process: process, IsSheetMetal: sheet);

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
```

Further facts (each its own `[Fact]`, exact expectations):
- `Classify_blank_sheet_metal_is_guessed_sheet_metal`: `N("CD-1.ipt", material: "PLA", sheet: true)`, isSheetMetal true -> `(SheetMetal, true)` (sheet metal checked before material).
- `Classify_blank_print_material_is_guessed_print`: material `"PETG Black"` -> `(Print, true)`.
- `Classify_blank_ipt_is_guessed_cnc`: `"Aluminum 6061"` -> `(Cnc, true)`; `Classify_blank_iam_is_guessed_other`: `N("CD-2.iam")` -> `(OtherMake, true)`.
- `Classify_none_cases`: Purchased structure; Phantom; Reference; category `"Part - Purchased"`; category `"Part - Content Center"`; path containing `\Content Center Files\`; virtual (`FileName = "", FullPath = ""`): each -> `(None, false)`, even with `process: "CNC"`.
- `FilesFor_table`: assert every (mode, kind) pair against the rule list above (20 combinations, `[Theory]`).
- `Select_dedupes_and_totals_quantities`: roots `A = N("CD-100.iam", 2, kids: [N("CD-200.ipt", 3), N("CD-300.ipt", 1, process: "Sheet Metal")])` and `N("CD-200.ipt", 4)`; `Select(roots, Cnc, Mats, _ => null)` -> one part, `Node.FileName == "CD-200.ipt"`, `TotalQty == 10`, `Item == BomOrdering.Flatten(roots).First(l => l.Node.FileName == "CD-200.ipt").Item`.
- `Select_prunes_children_of_buy_and_reference_rows_not_phantom`: roots `N("CD-400.iam", structure: "Purchased", kids: [N("CD-500.ipt")])`, `N("CD-410.iam", structure: "Reference", kids: [N("CD-510.ipt")])`, `N("CD-420.iam", structure: "Phantom", kids: [N("CD-520.ipt", 2)])`, and `N("CD-430.iam", kids: [N("CD-530.ipt")])` with `categoryOf` returning `"Part - Purchased"` for CD-430; AllMake without top -> file names exactly `{ "CD-520.ipt" }` with `TotalQty == 2`.
- `Select_all_make_prepends_top`: top `N("CD-001882.iam")`, roots `[N("CD-200.ipt"), N("CD-300.ipt", sheet: true)]` -> first part `Item == "Top"`, `Files == Step | Pdf`, `Class == (OtherMake, true)`; CD-300 `Files == Step | Pdf | Dxf`. Same call with mode Cnc ignores `top`.
- `Select_print_mode`: `N("CD-600.ipt", 3, material: "PLA")`, `N("CD-601.ipt", process: "3D Print", material: "Steel")`, `N("CD-602.ipt")` -> CD-600 and CD-601 only, `Files == Print`.
- `PrintMaterials_word_boundaries` (`[Theory]`): true for `"PLA"`, `"pla+"`, `"PC-ABS"`, `"Onyx (PA12-CF)"`, `"Nylon 12"`, `"TPU 95A"`, `"Tough Resin"`, `"PC"`; false for `"Generic Plastic"`, `"PCB laminate"`, `"Polycarbonate"`, `"Carbon Fiber Sheet"`, `"Steel, Mild"`, `""`, null.
- `FileStem_and_dests`: node `CD-000123.ipt` rev `"A"` -> `"CD-000123 Rev A"`, `Dest(Step) == "STEP/CD-000123 Rev A.stp"`, `Dest(Pdf) == "PDF/CD-000123 Rev A.pdf"`, `Dest(Dxf) == "DXF/CD-000123 Rev A.dxf"`; empty rev -> `"CD-000123 Rev -"`; rev `"A/B"` -> `"CD-000123 Rev A-B"`.
- `PrintDest_folder_per_material`: material `"PLA: Black"`, qty 4 -> `"PLA- Black/CD-000123 Rev A x4.3mf"` with extension `".3mf"`; qty 2.5 and `"stl"` -> `"... x2.5.stl"`; blank material -> folder `"No material"`.
- `PartnerError_*`: `null`, `""`, `"  "` -> `"Partner is required."`; `"Acme/West"`, `"Acme."`, `"A|B"` -> the invalid-name message; 61 letters -> the length message; `"Acme Machining"` -> null.
- `PartnerZipName_sanitizes`: `("CD-001882", "Acme Machining", Cnc)` -> `"CD-001882 Acme Machining CNC Package.zip"`; AllMake -> `"... Make Package.zip"`; SheetMetal -> `"... Sheet Metal Package.zip"`; partner `"A:B"` -> `"CD-001882 A-B CNC Package.zip"`; `PrintZipName("CD-001882") == "CD-001882 3D Print Package.zip"`.
- `PreviewText_counts`: AllMake parts from `Select_all_make_prepends_top` -> `"3 parts: 3 STEP, 3 PDF, 1 DXF"`; one Cnc part -> `"1 part: 1 STEP, 1 PDF, 0 DXF"`; Print with two parts -> `"2 parts: 2 print files"`.
- `PreviewText_for_empty_selection`: empty list with each mode -> the four "No ... found." strings above.
- `ProcessText_and_guessed_entry`: Process `" CNC "` -> `"CNC"`; blank guessed Cnc -> `"guessed: CNC"`; guessed OtherMake -> `"guessed: Other make"`; `GuessedEntry` -> `"CD-000123 (CNC)"`.
- `IsReleased`: `"Released"`, `" released "` true; `""`, null, `"Work in Progress"` false.
- `Manifest_json_contract`: build a `PackageManifestData` with one row, one file (stamp true), one entry in each list; parse with `JsonNode.Parse` and assert every contract key and value, `created == "2026-10-13 14:05"`, `rows[0]["qty"].GetValue<double>() == 4`, `files[0]["stamp"].GetValue<bool>()`.

`BridgeSettingsTests.cs` (temp dir per test, `IDisposable` like `BomTests`):
- `Defaults_values`: the three lists equal the Global Constraints values, `RecentPartners` empty.
- `Load_missing_writes_defaults`: returns defaults and the file now exists and parses with the three keys.
- `Load_malformed_uses_defaults_and_keeps_file`: file text `"{ not json"`; result equals defaults; file text unchanged; log received one line containing `settings.json`.
- `Load_partial_file_fills_missing_keys`: `{"printMaterials": ["PLA", 5, " ", "Wood "]}` -> PrintMaterials `["PLA", "Wood"]`, ProcessValues default.
- `WithRecent_orders_dedupes_and_caps`: `["B","a","C"]` + `"A"` -> `["A","B","C"]`; 10 names + new -> 10 items with the new one first; blank -> unchanged.
- `RememberPartner_preserves_other_keys`: file `{"processValues":["CNC"],"printMaterials":["PLA"],"recentPartners":["Old"],"extra":1}` -> after `RememberPartner(path, "New")` the file has `recentPartners ["New","Old"]`, `processValues ["CNC"]`, `extra 1`.
- `RememberPartner_malformed_returns_false_untouched`.

`OutputsTests.cs`:
- `PackageArgs_order`: `PackageArgs("m.json", "o.zip", "r.json")` equals `{"package","--manifest","m.json","--out","o.zip","--result","r.json"}`.
- `Counts_reads_integers`: `{"ok":true,"step":3,"pdf":2,"zip":"x"}` -> `step 3`, `pdf 2`, no `zip`; `null` and `"nope"` -> empty.
- `Parse_no_result_uses_public_message`: `ToolResults.Parse(null, "zip", "files").Error == ToolResults.NoResultMessage`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build errors for the missing types (PackageRules, PrintMaterials, BridgeSettings, BomNode.Process).

- [ ] **Step 3: Implement**

Add the BomNode trailing parameters and the `Process` column (`Field(n.Process)` between Material and File Name). Create `Packages.cs` and `BridgeSettings.cs` exactly per the interfaces and rules above (System.Text.Json / JsonNode, no new packages). In `Outputs.cs` rename `NoResult` to `public const string NoResultMessage` (same text) and add `PackageArgs` and `Counts`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test inventor-addin/SFVaultBridge.sln` then `dotnet build inventor-addin/SFVaultBridge.sln -c Release` and `py -3.14 -m pytest -q`
Expected: all green, Release build 0 errors (the add-in still compiles: new BomNode parameters are optional).

- [ ] **Step 5: Commit**

```bash
git add inventor-addin/SFVaultBridge.Core/Bom.cs inventor-addin/SFVaultBridge.Core/Packages.cs inventor-addin/SFVaultBridge.Core/BridgeSettings.cs inventor-addin/SFVaultBridge.Core/Outputs.cs inventor-addin/SFVaultBridge.Core.Tests/BomTests.cs inventor-addin/SFVaultBridge.Core.Tests/PackageRulesTests.cs inventor-addin/SFVaultBridge.Core.Tests/BridgeSettingsTests.cs inventor-addin/SFVaultBridge.Core.Tests/OutputsTests.cs
git commit -m "Bridge core: Process column, package rules, print materials, settings file, package manifest

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 2: Python: State column and yellow unreleased rows in review_package and bom_purchasing

**Files:**
- Modify: `bom_purchasing.py` (constants, `is_released`, yellow fill in `build_purchasing_sheet`)
- Modify: `review_package.py` (`BOM_COLUMNS` gains `State` after `REV`; new `bom_table_data`; `_bom_table` applies yellow rows)
- Test: `tests/test_review_package.py`, `tests/test_vault_tools_cli.py` (add tests; existing ones unchanged)

**Interfaces:**
- Produces: `bom_purchasing.UNRELEASED_FILL = "FFF59D"`; `bom_purchasing.is_released(value: Any) -> bool` (None, NaN and non-strings handled: `False` unless `str(value).strip().lower() == "released"`); `review_package.bom_table_data(bom: str) -> tuple[list[list[str]], list[int]]` returning the table data (header row first) and the table row indices (1-based in table coordinates, header is 0) to paint yellow.

Rules:
- review_package: `BOM_COLUMNS = ["Item", "Part Number", "Description", "QTY", "BOM Structure", "Material", "REV", "State"]` (columns still filtered by presence, so a BOM without State shows no State column). Yellow rows only when the BOM has a `State` column; then every data row with `not bp.is_released(state)` is yellow (blank included). `_bom_table` uses `bom_table_data` and appends `("BACKGROUND", (0, r), (-1, r), colors.HexColor("#FFF59D"))` per yellow row after the existing style commands. Applies in both review and assembly mode; nothing else changes.
- bom_purchasing `build_purchasing_sheet`: when `"State" in df.columns` and `not is_released(row.get("State"))`, the row's fill is `PatternFill("solid", fgColor=UNRELEASED_FILL)`, replacing the olive (assembly) or alternating fill. The unmatched orange (`UNMATCHED_FILL`, a Buy part with no price) still wins over yellow. By Vendor and Assembly Costs tabs have no State column and stay unchanged. No column, header, width or formula changes.

- [ ] **Step 1: Write the failing tests**

`tests/test_review_package.py` (append):

```python
STATE_HEADER = "Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tState\tMaterial\tProcess\tFile Name\tVendor\tVendor Number\r\n"


def _state_bom(path: Path) -> Path:
    rows = [("1", "CD-000010", "Released"), ("2", "CD-000011", "Work in Progress"),
            ("3", "CD-000012", ""), ("4", "CD-000013", " released ")]
    lines = [STATE_HEADER] + [f"{i}\t{pn}\tNormal\tEach\t1\tpart {i}\tA\t{st}\tSteel\t\t{pn}.ipt\t\t\r\n" for i, pn, st in rows]
    path.write_text("".join(lines), encoding="utf-8")
    return path


def test_bom_table_has_state_after_rev_and_yellow_unreleased_rows(tmp_path):
    data, yellow = rp.bom_table_data(str(_state_bom(tmp_path / "bom.txt")))
    assert data[0] == ["Item", "Part Number", "Description", "QTY", "BOM Structure", "Material", "REV", "State"]
    assert [r[-1].strip() for r in data[1:]] == ["Released", "Work in Progress", "", "released"]
    assert yellow == [2, 3]


def test_bom_without_state_column_has_no_yellow_rows(tmp_path):
    data, yellow = rp.bom_table_data(str(_bom(tmp_path / "bom.txt", 3)))
    assert "State" not in data[0] and yellow == []


def test_review_pdf_bom_page_shows_state(tmp_path):
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_state_bom(tmp_path / "bom.txt")), str(_manifest(tmp_path, [])), str(out))
    assert res["ok"] is True
    assert "State" in (PdfReader(str(out)).pages[1].extract_text() or "")
```

`tests/test_vault_tools_cli.py` (append; reuses `ADDIN_HEADERS` style but with the new Process column):

```python
YELLOW = "FFF59D"


def _fill(ws, row):
    return (ws.cell(row=row, column=1).fill.fgColor.rgb or "")[-6:].upper()


def test_unreleased_rows_are_yellow_and_unmatched_stays_orange(tmp_path):
    import openpyxl
    hdr = ("Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tState\tMaterial\tProcess\t"
           "File Name\tVendor\tVendor Number")
    rows = [
        "1\tCD-990010\tNormal\tEach\t1\tPlate\tA\tReleased\tSteel\t\tCD-990010.ipt\t\t",
        "2\tCD-990011\tNormal\tEach\t1\tBracket\tA\tWork in Progress\tSteel\t\tCD-990011.ipt\t\t",
        "3\tCD-990012\tNormal\tEach\t1\tSpacer\t\t\tSteel\t\tCD-990012.ipt\t\t",
        "4\tCD-990013\tNormal\tEach\t1\tShim\tA\t released \tSteel\t\tCD-990013.ipt\t\t",
        "5\tCD-990014\tNormal\tEach\t1\tSub\tA\tWork in Progress\t\t\tCD-990014.iam\t\t",
        "5.1\tCD-990015\tNormal\tEach\t1\tSub part\tA\tReleased\tSteel\t\tCD-990015.ipt\t\t",
        "6\tCD-990016\tPurchased\tEach\t1\tWidget\t\t\t\t\tCD-990016.ipt\t\t",
    ]
    bom = tmp_path / "bom.txt"
    bom.write_text(hdr + "\r\n" + "\r\n".join(rows) + "\r\n", encoding="utf-8")
    code = cli.main(["purchasing", "--bom", str(bom), "--assembly", "CD-990000", "--out", str(tmp_path),
                     "--name", "P.xlsx", "--result", str(tmp_path / "r.json")])
    assert code == 0, (tmp_path / "r.json").read_text(encoding="utf-8")
    ws = openpyxl.load_workbook(tmp_path / "P.xlsx")["Purchasing"]
    by_number = {}
    name_col = [c.value for c in ws[3]].index("Name") + 1
    for r in range(4, 4 + len(rows)):
        by_number[ws.cell(row=r, column=name_col).value] = _fill(ws, r)
    assert by_number["CD-990010"] != YELLOW
    assert by_number["CD-990011"] == YELLOW
    assert by_number["CD-990012"] == YELLOW
    assert by_number["CD-990013"] != YELLOW
    assert by_number["CD-990014"] == YELLOW          # yellow beats the olive assembly fill
    assert by_number["CD-990015"] != YELLOW
    assert by_number["CD-990016"] == "FCE4D6"        # unmatched orange beats yellow


def test_blank_state_rows_from_addin_bom_are_yellow(tmp_path):
    ws = _purchasing_sheet(tmp_path)   # existing helper: State column present, values blank
    assert _fill(ws, 4) == YELLOW      # CD-001882, Make, blank state
    assert _fill(ws, 5) == "FCE4D6"    # purchased bolt with no vendor: unmatched orange wins
```

The Name column falls back to the part number (pinned by the existing `test_name_column_is_filled_from_an_addin_bom`), which is why the first test keys rows by Name.

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -3.14 -m pytest -q tests/test_review_package.py tests/test_vault_tools_cli.py`
Expected: FAIL (`bom_table_data` missing, fills not yellow).

- [ ] **Step 3: Implement** per the rules above. In `build_purchasing_sheet`, compute `has_state = "State" in df.columns` once before the loop; in the loop after the olive/alt choice and before the unmatched override: `if has_state and not is_released(row.get("State")): fill = unreleased_fill`.

- [ ] **Step 4: Run the full suite**

Run: `py -3.14 -m pytest -q` (green, 870+ plus the new tests) and the C# gates.

- [ ] **Step 5: Commit**

```bash
git add bom_purchasing.py review_package.py tests/test_review_package.py tests/test_vault_tools_cli.py
git commit -m "BOM outputs: State column in the review BOM table, unreleased rows light yellow

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 3: Python: `package` subcommand (xlsx, cover, stamped PDFs, zip), exe rebuild, installer stale list

**Files:**
- Create: `manufacturing_package.py`
- Modify: `vault_tools_cli.py` (subcommand `package`, docstring spec list)
- Modify: `inventor-addin/Install-VaultBridge.ps1` (add `'manufacturing_package.py'` to `$sources`)
- Test: `tests/test_manufacturing_package.py` (new), `tests/test_vault_tools_cli.py` (CLI wiring)

**Interfaces:**
- Consumes: the Package manifest contract (Global Constraints); `bom_purchasing` constants `DARK_BLUE`, `MID_BLUE`, `PALE_BLUE`, `LIGHT_GRAY`, `GRAY_BDR`, `DARK_GRAY`, `WHITE`, `UNRELEASED_FILL`, `is_released`, `_border`, `_add_logo`; `review_package._doc`, `review_package._para`, `review_package.WATERMARK_COLOR`, `WATERMARK_OPACITY`, `UNRELEASED_TEXT`; `pdf_watermark.apply_watermark_to_pages`.
- Produces: `manufacturing_package.run_package(manifest: str, out_zip: str) -> dict[str, Any]`; CLI `SFVaultTools package --manifest M --out Z --result R`. Result on success:
  `{"ok": True, "mode": "package", "zip": str(out_zip), "files": <entries in zip>, "rows": <len(rows)>, "step": n, "pdf": n, "dxf": n, "print": n, "stamped": n, "failed": n, "guessed": n, "missing": n, "no_flat_pattern": n}`; on a rejected manifest `{"ok": False, "error": "<plain sentence>"}`.

Rules:
- Validate: `kind` must be `partner` or `print`, else error `Unknown package kind: <kind>.`. Each `files[].dest` must be relative, use `/`, contain no `..` segment, no drive letter, no leading `/`; else error `Bad package path: <dest>.` and no zip is written.
- Staging: `tempfile.mkdtemp(prefix="sfpkg-")`; removed in `finally`.
- Files: for each entry, if `src` is missing or unreadable, add `{"part": <dest file stem>, "reason": "file missing after export"}` to failed and drop that dest from every row's `files`; else copy to `staging/dest` (parents created). When `stamp` is true and dest ends `.pdf`, write `apply_watermark_to_pages(bytes, UNRELEASED_TEXT, range(page_count), font_size=120, color=WATERMARK_COLOR, opacity=WATERMARK_OPACITY)` instead of a plain copy; an unreadable PDF goes to failed with `PDF unreadable: <exc>`.
- Workbook: partner -> `BOM.xlsx`, sheet `BOM`, columns `Item, Part Number, Description, Qty, Material, Material Finish, Process, REV, State, File(s) in package`; print -> `Print List.xlsx`, sheet `Print List`, columns `Part Number, Description, Qty, Material, Process, REV, State, File`. Row 1 merged across all columns, dark blue, white bold Arial 11, centred, height 50, logo via `_add_logo`: partner title `<assembly> <partner> <mode> package`, print title `<assembly> 3D print list`. Row 2 merged, `Generated: <%B %d, %Y>` plus ` | <source>` when source is non-blank, italic grey 9 on light grey, right aligned. Row 3 headers (dark blue, white bold 10, wrap, thin borders, medium mid-blue top border). Data from row 4 in manifest order: Arial 10, thin borders, alternate pale blue/white, yellow `UNRELEASED_FILL` when `not is_released(state)`. Files cell: basenames joined with `, ` (partner) or the first basename (print). Qty written as a number (int when whole). Autofilter over header + data, freeze panes `A4`, sensible widths (Description 44, Files 48, others 12 to 22).
- Cover (partner only), `00 Cover.pdf`, built with `rp._doc` / `rp._para` (escaped): title `Partner package: <assembly>`, heading `<description>`, lines `Partner: <partner>`, `Parts: <mode label>` where the label is `All Make parts` for `Make`, `CNC (machined)` for `CNC`, `Sheet metal` for `Sheet Metal`; `Assembly <assembly> Rev <revision or -> (<state or unknown>)`, `Created <created> by <user>`, counts line `<rows> parts: <step> STEP, <pdf> PDF, <dxf> DXF`; then sections in this order, each `None` when empty: `Process not set (guessed)` (the `guessed` list), `Missing drawings`, `No flat pattern`, `Could not export` (`<part>: <reason>`, including failures added by this run).
- Zip: `zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED)`; add every file under staging with arcname relative to staging using `/`, root files first (`00 Cover.pdf`, then the xlsx), then the rest sorted case-insensitively; never add directory entries (so empty `STEP/`, `PDF/`, `DXF/` never appear). Write to `out_zip + ".part"` then `os.replace` to `out_zip`.
- `failed` in the result counts the manifest's `failed` entries plus failures added by this run; `guessed`, `missing` and `no_flat_pattern` are the manifest list lengths.
- Counts: `step` dests ending `.stp`, `pdf` dests under `PDF/`, `dxf` ending `.dxf`, `print` ending `.3mf` or `.stl`, counted over files actually written.

- [ ] **Step 1: Write the failing tests** (`tests/test_manufacturing_package.py`)

```python
import json
import zipfile
from io import BytesIO
from pathlib import Path

import openpyxl
from pypdf import PdfReader
from reportlab.pdfgen import canvas

import manufacturing_package as mp
import vault_tools_cli as cli

YELLOW = "FFF59D"


def _pdf(path: Path, label: str, pages: int = 1) -> Path:
    c = canvas.Canvas(str(path), pagesize=(1224, 792))
    for p in range(pages):
        c.drawString(100, 700, f"{label} sheet {p + 1}")
        c.showPage()
    c.save()
    return path


def _partner_manifest(tmp_path: Path, **over) -> Path:
    stp = tmp_path / "001.stp"; stp.write_bytes(b"ISO-10303-21;")
    released = _pdf(tmp_path / "002.pdf", "PLATE")
    draft = _pdf(tmp_path / "003.pdf", "BRACKET", pages=2)
    m = {
        "kind": "partner", "assembly": "CD-001882", "description": "Big blue adapter", "revision": "A",
        "state": "Released", "partner": "Acme Machining", "mode": "CNC",
        "source": "Model state: Primary | Design view: Default | BOM view: Structured",
        "user": "zak", "created": "2026-10-13 14:05",
        "rows": [
            {"item": "1", "part_number": "CD-000123", "description": "Plate", "qty": 4, "material": "Aluminum 6061",
             "finish": "As Finished", "process": "CNC", "revision": "A", "state": "Released",
             "files": ["STEP/CD-000123 Rev A.stp", "PDF/CD-000123 Rev A.pdf"]},
            {"item": "2", "part_number": "CD-000124", "description": "Bracket", "qty": 1, "material": "Steel",
             "finish": "", "process": "guessed: CNC", "revision": "", "state": "Work in Progress",
             "files": ["PDF/CD-000124 Rev -.pdf"]},
        ],
        "files": [
            {"src": str(stp), "dest": "STEP/CD-000123 Rev A.stp", "stamp": False},
            {"src": str(released), "dest": "PDF/CD-000123 Rev A.pdf", "stamp": False},
            {"src": str(draft), "dest": "PDF/CD-000124 Rev -.pdf", "stamp": True},
        ],
        "guessed": ["CD-000124 (CNC)"], "missing_drawings": ["CD-000125"], "no_flat_pattern": [],
        "failed": [{"part": "CD-000126", "reason": "STEP export failed: boom"}],
    }
    m.update(over)
    p = tmp_path / "manifest.json"
    p.write_text(json.dumps(m), encoding="utf-8")
    return p


def test_partner_zip_layout_and_counts(tmp_path):
    out = tmp_path / "CD-001882 Acme Machining CNC Package.zip"
    res = mp.run_package(str(_partner_manifest(tmp_path)), str(out))
    assert res["ok"] is True, res
    names = zipfile.ZipFile(out).namelist()
    assert names[:2] == ["00 Cover.pdf", "BOM.xlsx"]
    assert set(names) == {"00 Cover.pdf", "BOM.xlsx", "STEP/CD-000123 Rev A.stp",
                          "PDF/CD-000123 Rev A.pdf", "PDF/CD-000124 Rev -.pdf"}
    assert not any(n.endswith("/") for n in names) and not any(n.startswith("DXF") for n in names)
    assert (res["step"], res["pdf"], res["dxf"], res["print"], res["stamped"]) == (1, 2, 0, 0, 1)
    assert res["files"] == 5 and res["rows"] == 2


def test_partner_bom_xlsx_title_source_state_and_yellow(tmp_path):
    out = tmp_path / "p.zip"
    mp.run_package(str(_partner_manifest(tmp_path)), str(out))
    ws = openpyxl.load_workbook(BytesIO(zipfile.ZipFile(out).read("BOM.xlsx")))["BOM"]
    assert ws["A1"].value == "CD-001882 Acme Machining CNC package"
    assert ws["A2"].value.startswith("Generated: ") and ws["A2"].value.endswith(" | Model state: Primary | Design view: Default | BOM view: Structured")
    assert [c.value for c in ws[3]] == ["Item", "Part Number", "Description", "Qty", "Material", "Material Finish",
                                        "Process", "REV", "State", "File(s) in package"]
    assert ws["G5"].value == "guessed: CNC" and ws["I5"].value == "Work in Progress"
    assert ws["J4"].value == "CD-000123 Rev A.stp, CD-000123 Rev A.pdf"
    assert (ws["A4"].fill.fgColor.rgb or "")[-6:] != YELLOW
    assert (ws["A5"].fill.fgColor.rgb or "")[-6:] == YELLOW
    assert ws["D4"].value == 4


def test_stamp_only_flagged_pdfs(tmp_path):
    out = tmp_path / "p.zip"
    mp.run_package(str(_partner_manifest(tmp_path)), str(out))
    z = zipfile.ZipFile(out)
    stamped = PdfReader(BytesIO(z.read("PDF/CD-000124 Rev -.pdf")))
    plain = PdfReader(BytesIO(z.read("PDF/CD-000123 Rev A.pdf")))
    assert all("NOT RELEASED" in (p.extract_text() or "") for p in stamped.pages) and len(stamped.pages) == 2
    assert all("NOT RELEASED" not in (p.extract_text() or "") for p in plain.pages)


def test_cover_lists(tmp_path):
    out = tmp_path / "p.zip"
    mp.run_package(str(_partner_manifest(tmp_path)), str(out))
    text = "".join(p.extract_text() or "" for p in PdfReader(BytesIO(zipfile.ZipFile(out).read("00 Cover.pdf"))).pages)
    for s in ("Partner package: CD-001882", "Partner: Acme Machining", "Parts: CNC (machined)",
              "Rev A (Released)", "Process not set (guessed)", "CD-000124 (CNC)", "Missing drawings", "CD-000125",
              "No flat pattern", "Could not export", "CD-000126: STEP export failed: boom", "2 parts: 1 STEP, 2 PDF, 0 DXF"):
        assert s in text, s


def test_missing_src_goes_to_failed_and_out_of_row(tmp_path):
    m = json.loads(_partner_manifest(tmp_path).read_text(encoding="utf-8"))
    m["files"][0]["src"] = str(tmp_path / "gone.stp")
    p = tmp_path / "m2.json"; p.write_text(json.dumps(m), encoding="utf-8")
    out = tmp_path / "p.zip"
    res = mp.run_package(str(p), str(out))
    assert res["ok"] is True and res["step"] == 0 and res["failed"] == 2
    ws = openpyxl.load_workbook(BytesIO(zipfile.ZipFile(out).read("BOM.xlsx")))["BOM"]
    assert ws["J4"].value == "CD-000123 Rev A.pdf"


def test_bad_dest_is_rejected_and_no_zip(tmp_path):
    for bad in ("../evil.stp", "C:/x.stp", "/abs.stp", "STEP/../../x.stp"):
        m = json.loads(_partner_manifest(tmp_path).read_text(encoding="utf-8"))
        m["files"][0]["dest"] = bad
        p = tmp_path / "bad.json"; p.write_text(json.dumps(m), encoding="utf-8")
        out = tmp_path / "bad.zip"
        res = mp.run_package(str(p), str(out))
        assert res["ok"] is False and "Bad package path" in res["error"] and not out.exists()


def test_print_package_layout_and_list(tmp_path):
    f = tmp_path / "a.3mf"; f.write_bytes(b"3mf")
    m = {"kind": "print", "assembly": "CD-001882", "mode": "3D Print",
         "rows": [{"item": "3", "part_number": "CD-000200", "description": "Clip", "qty": 4, "material": "PLA",
                   "process": "3D Print", "revision": "", "state": "", "files": ["PLA/CD-000200 Rev - x4.3mf"]}],
         "files": [{"src": str(f), "dest": "PLA/CD-000200 Rev - x4.3mf", "stamp": False}]}
    p = tmp_path / "m.json"; p.write_text(json.dumps(m), encoding="utf-8")
    out = tmp_path / "CD-001882 3D Print Package.zip"
    res = mp.run_package(str(p), str(out))
    assert res["ok"] is True and res["print"] == 1
    names = zipfile.ZipFile(out).namelist()
    assert names == ["Print List.xlsx", "PLA/CD-000200 Rev - x4.3mf"]
    ws = openpyxl.load_workbook(BytesIO(zipfile.ZipFile(out).read("Print List.xlsx")))["Print List"]
    assert ws["A1"].value == "CD-001882 3D print list"
    assert "|" not in ws["A2"].value
    assert [c.value for c in ws[3]] == ["Part Number", "Description", "Qty", "Material", "Process", "REV", "State", "File"]
    assert ws["H4"].value == "CD-000200 Rev - x4.3mf"
    assert (ws["A4"].fill.fgColor.rgb or "")[-6:] == YELLOW


def test_unknown_kind_is_rejected(tmp_path):
    p = _partner_manifest(tmp_path, kind="traveler")
    res = mp.run_package(str(p), str(tmp_path / "x.zip"))
    assert res["ok"] is False and "Unknown package kind" in res["error"]


def test_cli_package_writes_result(tmp_path):
    out = tmp_path / "p.zip"
    r = tmp_path / "r.json"
    code = cli.main(["package", "--manifest", str(_partner_manifest(tmp_path)), "--out", str(out), "--result", str(r)])
    res = json.loads(r.read_text(encoding="utf-8"))
    assert code == 0 and res["ok"] is True and res["mode"] == "package" and Path(res["zip"]) == out
```

- [ ] **Step 2: Run to verify failure**: `py -3.14 -m pytest -q tests/test_manufacturing_package.py` -> FAIL (`No module named 'manufacturing_package'`).

- [ ] **Step 3: Implement** `manufacturing_package.py` per the rules (module docstring names the spec). In `vault_tools_cli.py` add the subparser (`--manifest`, `--out`, `--result`, all required) and the branch `import manufacturing_package; result = manufacturing_package.run_package(args.manifest, args.out)`. Add `'manufacturing_package.py'` to `$sources` in `Install-VaultBridge.ps1` (do not run the installer).

- [ ] **Step 4: Run the full suite and gates**: `py -3.14 -m pytest -q`; C# gates.

- [ ] **Step 5: Rebuild the exe and smoke-test it**

```bash
rm -f inventor-addin/tools/SFVaultTools.exe
cmd //c ".\build_vault_tools_exe.bat"
```

Then, in the scratchpad, copy the manifest and files from `test_partner_zip_layout_and_counts` (write them with a short `py -3.14` script) and run `inventor-addin/tools/SFVaultTools.exe package --manifest <m> --out <zip> --result <r>`; wait for the process to exit; show the result JSON and `zipfile` namelist in the report. Also run `SFVaultTools.exe review` once with an existing test manifest to confirm the review path still works.

- [ ] **Step 6: Commit**

```bash
git add manufacturing_package.py vault_tools_cli.py inventor-addin/Install-VaultBridge.ps1 tests/test_manufacturing_package.py
git commit -m "SFVaultTools package: BOM.xlsx or Print List.xlsx, cover, NOT RELEASED stamps, zip

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 4: Add-in: BomReader Process/finish/sheet-metal, enricher categories, DocumentOpener, DrawingFinder, ModelExporter

**Files:**
- Modify: `inventor-addin/SFVaultBridge.Addin/BomReader.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/BomEnricher.cs`
- Create: `inventor-addin/SFVaultBridge.Addin/DocumentOpener.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/DrawingExporter.cs` (use DocumentOpener; behaviour unchanged)
- Create: `inventor-addin/SFVaultBridge.Addin/DrawingFinder.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (BuildPackage's local `Candidates` replaced by `DrawingFinder.Candidates`; nothing else)
- Create: `inventor-addin/SFVaultBridge.Addin/ModelExporter.cs`

**Interfaces:**
- Consumes: `BomNode` (Task 1), `ReviewRules.PickDrawings`, `VaultFiles.WhereUsedDrawings`, `VaultFiles.DrawingsNamed`.
- Produces:

```csharp
// BomReader: Read() now fills Process, MaterialFinish, IsSheetMetal on every node.
internal static BomNode TopNode(Inv.AssemblyDocument asm, string revision, string state);
//   PartNumber = file stem, Description/Material/Process/MaterialFinish from asm iProperties, Quantity 1, "Each", "Normal",
//   FileName/FullPath from asm.FullFileName, no children, Revision/State as given.

// BomEnricher
public static IReadOnlyList<BomNode> Apply(IReadOnlyList<BomNode> roots, string workingRoot, Action<string> log,
                                           IDictionary<string, string?>? categories = null); // records Vault category per FullPath (null when unknown)

internal sealed class OpenedDocument : IDisposable
{
    public Inv.Document Document { get; }
    public bool OpenedHere { get; }
    public void Dispose();  // closes (without saving) only when OpenedHere; never throws
}
internal static class DocumentOpener
{
    public static Inv.Document? FindOpen(Inv.Application app, string path);   // moved from DrawingExporter
    public static OpenedDocument Open(Inv.Application app, string path);      // invisible open; reuses an open doc; same OpenIds rule as today
}
// DrawingExporter.FindOpen stays as a one-line wrapper over DocumentOpener.FindOpen (RibbonActions calls it).

internal static class DrawingFinder
{
    public static IReadOnlyList<WsFile> Candidates(WsFile model, Action<string> log); // where-used + same-name drawings, deduped by MasterId
    public static WsFile? PartDrawing(WsFile model, Action<string> log);             // ReviewRules.PickDrawings(...).FirstOrDefault() mapped back to the file
}

internal sealed record PrintFormat(Inv.TranslatorAddIn Translator, string Extension); // ".3mf" or ".stl"
internal static class ModelExporter
{
    public const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";
    public const string StlTranslatorId = "{533E9A98-FC3B-11D4-8E7E-0010B541CD80}";
    public static void Step(Inv.Application app, string modelPath, string outStp, Action<string> log);
    public static bool FlatPatternDxf(Inv.Application app, string partPath, string outDxf, Action<string> log); // false: no flat pattern
    public static PrintFormat ResolvePrintFormat(Inv.Application app, Action<string> log);
    public static void Print(Inv.Application app, PrintFormat format, string partPath, string outFile, Action<string> log);
}
```

Implementation notes (compile-verify every Inventor/Vault call; no full bodies required here):
- BomReader `Node`: `process = Prop("Inventor User Defined Properties", "Process")`, `finish = Prop("Inventor User Defined Properties", "Material Finish")`; sheet metal when the row's document is a part whose `SubType` equals `"{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}"` (Inventor's sheet metal part subtype; read inside try, false on any error). Pass them as the new named BomNode arguments. No other change to Read.
- BomEnricher: when `categories` is non-null, set `categories[n.FullPath] = facts?.Category` inside `Lookup` (after the cache write). Existing callers pass nothing and behave as before.
- DocumentOpener.Open: same algorithm as DrawingExporter.Export today: `SilentOperation = true` for the open, `FindOpen` first, else `OpenIds` before, `Documents.Open(path, false)`, `OpenedHere` only when the InternalName was not in the before-set (and the before-set was readable). Restore `SilentOperation` in the same scope it was set. DrawingExporter.Export then becomes: activate the PDF translator, `using var d = DocumentOpener.Open(app, drawingPath)`, same options, same checks. Assembly Package and Design Review behaviour must be identical.
- ModelExporter.Step: translator by `StepTranslatorId`, activate, `using var d = DocumentOpener.Open(...)`; when `HasSaveCopyAsOptions` returns true set `options.Value["ApplicationProtocolType"] = 3` (AP214); log the option names once per run (`NameValueMap.Name[i]` for `i = 1..Count`) so the AP value can be confirmed on the first live run; `SaveCopyAs`; throw `BridgeException("export_failed", "Inventor did not write the STEP file.")` when the file is missing. For an assembly the referenced children resolve from the workspace because the top assembly is open.
- FlatPatternDxf: open; `d.Document is Inv.PartDocument p && p.ComponentDefinition is Inv.SheetMetalComponentDefinition smcd`; `if (!smcd.HasFlatPattern) return false`; `smcd.DataIO.WriteDataToFile("FLAT PATTERN DXF?AcadVersion=2018&OuterProfileLayer=Outer", outDxf)`; missing output -> `BridgeException("export_failed", "Inventor did not write the DXF.")`; return true. Never call `Unfold` or create a flat pattern.
- ResolvePrintFormat: iterate `app.ApplicationAddIns`; first `Inv.TranslatorAddIn` whose `DisplayName` contains `3MF` (OrdinalIgnoreCase) -> log `3MF translator: <DisplayName> <ClassIdString>` and return `(t, ".3mf")`; else `ItemById[StlTranslatorId]` -> `(t, ".stl")`; neither -> `BridgeException("no_print_translator", "Inventor has neither a 3MF nor an STL translator.")`.
- Print: activate, open, options: for STL `OutputFileType = 0` (binary) and `Resolution = 0` (high); for 3MF set nothing; log option names once; `SaveCopyAs`; missing output -> `BridgeException("export_failed", ...)`.
- DrawingFinder.Candidates is the exact body of `Candidates` inside `RibbonActions.BuildPackage` (log text `review name search for <name> failed`), moved; BuildPackage calls it.

- [ ] **Step 1:** Make the changes above.
- [ ] **Step 2:** Gates: Release build 0 errors and no new warnings in the touched files; `dotnet test inventor-addin/SFVaultBridge.sln` green; `py -3.14 -m pytest -q` green.
- [ ] **Step 3:** Diff review: confirm `git diff inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` only swaps the `Candidates` local function for `DrawingFinder.Candidates`, and DrawingExporter's PDF options are byte-identical.
- [ ] **Step 4: Commit**

```bash
git add inventor-addin/SFVaultBridge.Addin/BomReader.cs inventor-addin/SFVaultBridge.Addin/BomEnricher.cs inventor-addin/SFVaultBridge.Addin/DocumentOpener.cs inventor-addin/SFVaultBridge.Addin/DrawingExporter.cs inventor-addin/SFVaultBridge.Addin/DrawingFinder.cs inventor-addin/SFVaultBridge.Addin/RibbonActions.cs inventor-addin/SFVaultBridge.Addin/ModelExporter.cs
git commit -m "Add-in: BOM Process and sheet metal flag, STEP/DXF/3MF/STL model exporter, shared document opener

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 5: Add-in: PackageBuilder, Partner Package form, button and icon

**Files:**
- Create: `inventor-addin/SFVaultBridge.Addin/PackageBuilder.cs`
- Create: `inventor-addin/SFVaultBridge.Addin/PartnerPackageForm.cs`
- Create: `inventor-addin/SFVaultBridge.Addin/Downloads.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (Partner Package button; `Locate` becomes `internal static`; `KeepOutput` delegates to `Downloads.Keep`)
- Modify: `inventor-addin/SFVaultBridge.Addin/Icons.cs` (`BoxOut`)

**Interfaces:**
- Consumes: everything from Tasks 1 and 4; `VaultFiles.FindLatest`, `StateOf`, `RevisionOf`, `LocalPathOf`, `AcquireLatest`, `Publish`, `WorkingFolderRoot`; `VaultSession.Connection.UserName`; `ToolRunner.Run`; `ToolResults.PackageArgs`, `Parse`, `Counts`, `NoResultMessage`; `BomReader.SourceLine`.
- Produces:

```csharp
internal static class Downloads
{
    public static string Folder { get; }                                  // %USERPROFILE%\Downloads\SF Vault Bridge
    public static string? Keep(string tempOutput, string name, Action<string> log); // current KeepOutput body, moved
}

internal sealed record PackageContext(Inv.AssemblyDocument Assembly, string Folder, string VaultFolder, string Stem,
                                      WsFile? AssemblyFile, BomNode Top, IReadOnlyList<BomNode> Roots,
                                      IReadOnlyDictionary<string, string?> Categories, string Source,
                                      BridgeSettings Settings, PrintMaterials Materials);
internal sealed record PackageOutcome(string ZipName, string? VaultPath, int? Version, string? DownloadsCopy,
                                      string Summary, IReadOnlyList<string> Warnings);

internal sealed class PackageBuilder
{
    public PackageBuilder(Inv.Application app, Action<string> log);
    public PackageContext Prepare(Inv.AssemblyDocument asm);   // Locate, BomReader.Read, BomEnricher.Apply(..., categories), settings, Top node
    public IReadOnlyList<PackagePart> Parts(PackageContext ctx, ManufacturingMode mode);
    //   => PackageRules.Select(ctx.Roots, mode, ctx.Materials, n => ctx.Categories.TryGetValue(n.FullPath, out var c) ? c : null,
    //                          mode == ManufacturingMode.AllMake ? ctx.Top : null)
    public PackageOutcome Build(PackageContext ctx, ManufacturingMode mode, string partner, IReadOnlyList<PackagePart> parts);
}

internal sealed class PartnerPackageForm : Form
{
    public PartnerPackageForm(IReadOnlyList<string> recentPartners, Func<ManufacturingMode, IReadOnlyList<PackagePart>> partsFor);
    public string? Partner { get; }              // trimmed, set on Build
    public ManufacturingMode Mode { get; }       // AllMake, Cnc or SheetMetal
    internal static PartnerPackageForm Preview(IReadOnlyList<string> recent, Func<ManufacturingMode, IReadOnlyList<PackagePart>> partsFor);
}

// Icons
public static Bitmap BoxOut(int size);  // isometric box (same faces and colours as BoxDown) with a green arrow leaving it up and to the right
```

Build rules (PackageBuilder.Build):
1. `ctx.Settings` was read at Prepare (read on each run). Temp folder `%TEMP%\SFVaultBridge\<guid>` with `export\` inside; deleted in `finally`.
2. Status bar: set `_app.StatusBarText` at each step: `Partner Package: reading BOM`, `Partner Package: exporting <i> of <n>: <PartNumber>`, `Partner Package: building BOM and cover`, `Partner Package: checking in`; restore the previous text in `finally`. (3D Print uses the `3D Print Package:` prefix.)
3. Per part, in order, each in its own try/catch (a failure adds `PackageFailure(part.Node.PartNumber, "<STEP|DXF|drawing|print> export failed: <message>")` and continues):
   - Vault model: `VaultFiles.FindLatest(VaultPaths.ToVaultFolder(node.FullPath, root) + "/" + node.FileName)`; null -> failure `not in Vault` and skip the part. Local path: `VaultFiles.LocalPathOf(model)`; when `DocumentOpener.FindOpen(app, local)` is null, `VaultFiles.AcquireLatest(new[] { model })` (no children, never overwrites, same as today).
   - STEP when `Files` has Step: `ModelExporter.Step` to `export\NNN.stp`; dest `PackageRules.Dest(PackageFiles.Step, part)`.
   - DXF when `Files` has Dxf: `ModelExporter.FlatPatternDxf`; false -> add `node.PartNumber` to `noFlatPattern`.
   - PDF when `Files` has Pdf: `DrawingFinder.PartDrawing(model)`; null -> `missingDrawings.Add(node.PartNumber)`; else acquire as above if not open, `DrawingExporter.Export`, `stamp = open in Inventor || drawing.CheckedOut || !PackageRules.IsReleased(VaultFiles.StateOf(drawing))`.
   - Print when `Files` has Print: `ModelExporter.Print` with the format resolved once per run; dest `PackageRules.PrintDest(part, format.Extension)`.
   - Ensure dest uniqueness within the run with `VaultPaths.UniqueName` over a case-insensitive set of used dests.
   - Each successfully written dest goes into the row's `Files` and the manifest `files`.
4. Rows: `PackageRow(part.Item, node.PartNumber, node.Description, part.TotalQty, node.Material, node.MaterialFinish, PackageRules.ProcessText(part), node.Revision, node.State, files)`; `guessed` = `PackageRules.GuessedEntry(part)` for every part with `Class.Guessed`.
5. Manifest via `PackageManifest.ToJson(new PackageManifestData(mode == Print ? "print" : "partner", ctx.Stem, description, revision, state, partner, PackageRules.ModeName(mode), ctx.Source, user, DateTime.Now, ...))`; revision/state are the assembly file's Vault values (`VaultFiles.RevisionOf`, `VaultFiles.StateOf`, blank when not found).
6. Zip name: `PackageRules.PartnerZipName(ctx.Stem, partner, mode)` or `PrintZipName`. Run `ToolRunner.Run(ToolResults.PackageArgs(manifest, tempZip, resultPath), resultPath, TimeSpan.FromMinutes(10))`; parse with `ToolResults.Parse(json, "zip", "files")`. Not ok and error equals `NoResultMessage` -> `BridgeException("tool_failed", "SFVaultTools.exe is out of date or failed. Close Inventor and rerun Install-VaultBridge.ps1. Details are in bridge.log.")`; other errors -> `BridgeException("tool_failed", result.Error)`.
7. Downloads copy first: `Downloads.Keep(tempZip, zipName, log)` (never overwrites, `(n)` suffix); then `VaultFiles.Publish(tempZip, Path.Combine(ctx.Folder, zipName), ctx.VaultFolder, zipName, comment)` with comment `<Partner> <Mode> package for <stem>` (print: `3D print package for <stem>`). A publish exception becomes a warning `Not checked in to Vault: <message>` (the checkout guard message is kept as is); if the Downloads copy also failed, rethrow as `BridgeException("vault_failed", ...)` so the user knows nothing was kept.
8. `BridgeSettings.RememberPartner(BridgeSettings.DefaultPath, partner, log)` after the zip exists (partner mode only).
9. Summary from `ToolResults.Counts`: partner `<rows> parts: <step> STEP, <pdf> PDF, <dxf> DXF. <guessed> guessed, <missing> missing drawings, <no_flat_pattern> no flat pattern, <failed> could not export.`; print `<rows> parts: <print> print files (<3MF|STL>). <guessed> guessed, <failed> could not export.`

Form rules (PartnerPackageForm), same DPI-safe pattern as ImportPartForm (`AutoScaleMode.Dpi`, `AutoScaleDimensions 96`, FixedDialog, AutoSize GrowAndShrink, TableLayoutPanel two columns, `InputWidth = 380`, message box font):
- **Partner**: `ComboBox` `DropDownStyle.DropDown` filled with `recentPartners` (most recent first), text empty initially, TabIndex 0.
- **Parts**: `ComboBox` DropDownList with `All Make parts`, `CNC (machined)`, `Sheet metal`, default `All Make parts`.
- **Preview** label: `PackageRules.PreviewText(partsFor(mode), mode)`, recomputed on Parts change (cache per mode).
- Error label (firebrick). **Build** (AcceptButton, `DialogResult.None`) and **Cancel**.
- Build click: `PackageRules.PartnerError(text)` non-null -> show it in the error label, stay open; zero parts for the mode -> Build is disabled (set on load and on every mode change). Otherwise set `Partner`, `Mode`, `DialogResult.OK`.
- Never throws into Inventor; `partsFor` failures are logged by the caller and shown in the error label.

RibbonActions:
- `Add("Partner Package", "SFVaultBridge.PartnerPackage", "Build a zip of STEP models, PDF drawings, DXF flat patterns and a BOM for a manufacturing partner, and check it into Vault.", Icons.BoxOut, PartnerPackage);` placed after Assembly Package and before Import New Part. Update the class summary comment to list the buttons.
- `PartnerPackage(asm)`: `var b = new PackageBuilder(_app, _log); var ctx = b.Prepare(asm);` show the form with owner window; Cancel -> return; `var parts = b.Parts(ctx, form.Mode);` `var o = b.Build(ctx, form.Mode, form.Partner!, parts);` then `ShowOutcome(o)`.
- `ShowOutcome(PackageOutcome o)`: text `"<ZipName>\n" + (VaultPath is null ? "" : $"Vault: {VaultPath}{(Version is null ? "" : $" (version {Version})")}\n") + (DownloadsCopy is null ? "" : $"Copy: {DownloadsCopy}\n") + Summary + warnings joined by "\n" + "\n\nOpen folder?"`, Yes/No; Yes runs `explorer.exe /select,"<DownloadsCopy>"` (falls back to `Downloads.Folder` when the copy is null); failures are logged and shown as `Could not open the folder.`.
- `KeepOutput` keeps its signature and calls `Downloads.Keep`.

- [ ] **Step 1:** Implement Downloads, PackageBuilder, PartnerPackageForm, the icon and the button per the rules above.
- [ ] **Step 2:** Gates (Release build 0 errors, dotnet test green, pytest green).
- [ ] **Step 3:** Diff review: Purchasing Sheet, Design Review and Assembly Package code paths unchanged apart from the `KeepOutput` delegation; no new `MessageBox` without the owner window.
- [ ] **Step 4: Commit**

```bash
git add inventor-addin/SFVaultBridge.Addin/PackageBuilder.cs inventor-addin/SFVaultBridge.Addin/PartnerPackageForm.cs inventor-addin/SFVaultBridge.Addin/Downloads.cs inventor-addin/SFVaultBridge.Addin/RibbonActions.cs inventor-addin/SFVaultBridge.Addin/Icons.cs
git commit -m "Ribbon: Partner Package button, form with recent partners and part choice, zip into Vault and Downloads

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 6: Add-in: 3D Print Package button and icon, README

**Files:**
- Modify: `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (3D Print Package button)
- Modify: `inventor-addin/SFVaultBridge.Addin/Icons.cs` (`PrintNozzle`)
- Modify: `inventor-addin/README.md`

**Interfaces:**
- Consumes: `PackageBuilder.Prepare/Parts/Build`, `ShowOutcome`, `PackageRules.PrintZipName` (Task 5).
- Produces: `Icons.PrintNozzle(int size)`: a printer nozzle (ink-outlined trapezoid hot end with a small orange `#EF6C00` tip) above a block drawn as three stacked layers in the grey tones `BoxDown` uses; flat, transparent, legible at 16 and 32.

Rules:
- `Add("3D Print Package", "SFVaultBridge.PrintPackage", "Build a zip of 3MF (or STL) print files, one per 3D printed part, grouped by material, and check it into Vault.", Icons.PrintNozzle, PrintPackage);` right after Partner Package.
- `PrintPackage(asm)`: Prepare; `parts = Parts(ctx, ManufacturingMode.Print)`; zero -> `Msg("No 3D print parts found (Process = 3D Print or a print material).", "3D Print Package")` and return before anything is exported or written; else `Build(ctx, Print, "", parts)` and `ShowOutcome`.
- README:
  - Buttons intro: list the six action buttons (Purchasing Sheet, Design Review, Assembly Package, Partner Package, 3D Print Package, Import New Part) plus the status dot; note the State column and light yellow unreleased rows in every BOM (Purchasing tab, Design Review and Assembly Package BOM pages, Partner BOM.xlsx, Print List.xlsx).
  - `### Partner Package`: form fields, the three Parts choices and which files each gives (All Make parts: STEP for every Make part and Make sub-assembly plus the top assembly, PDF drawing where one exists, DXF for sheet metal; CNC: STEP + PDF; Sheet metal: DXF + PDF), selection by Process then fallback rules (sheet metal part, print material, `.ipt` machined, `.iam` other), Buy/Phantom/Reference/Content Center/virtual skipped, children of purchased sub-assemblies skipped, Qty is the total in the assembly, NOT RELEASED stamp rule, file names, zip name and layout, cover lists, Vault check-in plus `Downloads\SF Vault Bridge` copy, recent partners.
  - `### 3D Print Package`: selection, 3MF when Inventor has a 3MF translator else binary high-resolution STL, `<CD> Rev <rev> x<qty>` names, folder per material, Print List.xlsx, the "No 3D print parts found" message.
  - `### Process property`: values, matching rule, the settings file path and its three keys with defaults, malformed-file behaviour; Vault UDP steps for Zak: Vault Explorer > Tools > Administration > Vault Settings > Behaviors > Properties > New; Name `Process`, Type Text, List values `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other`; Associations: categories Part - Engineering and Assembly - Engineering; Mapping: Provider Inventor, file types `.ipt` and `.iam`, property `Process` (User Defined), Mapping Direction Read and Write (bidirectional), Create ticked; OK, then re-index is not needed for new check-ins.
  - Fix data card: add the example `py -3.14 scripts/vault_bridge.py fix-data-card CD-001885.ipt --custom Process=CNC` (and the `--apply` form), noting it is `.ipt` only.
- [ ] **Step 1:** Implement the button, icon and README.
- [ ] **Step 2:** Gates (Release build 0 errors, dotnet test green, pytest green). Grep everything this plan changed for the em dash character and fix any hit: `git diff <plan commit>..HEAD | LC_ALL=C.UTF-8 grep -nP '\x{2014}'` must print nothing, where `<plan commit>` is the commit that added this plan file (`git log -1 --format=%h -- docs/superpowers/plans/2026-10-13-ribbon-manufacturing-packages.md`).
- [ ] **Step 3: Commit**

```bash
git add inventor-addin/SFVaultBridge.Addin/RibbonActions.cs inventor-addin/SFVaultBridge.Addin/Icons.cs inventor-addin/README.md
git commit -m "Ribbon: 3D Print Package button; README for packages, Process property and Vault UDP steps

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Live check with Zak (after all tasks; not run by the implementer)

On CD-001882, after Zak installs: Partner Package in each of the three modes (check zip layout, BOM.xlsx yellow rows, cover lists, NOT RELEASED on unreleased drawings, Downloads copy, Vault version on rerun), 3D Print Package, and the logged STEP/STL/3MF option names and 3MF ClassId in bridge.log.

---

### Task 7: Dropbox upload for Partner Packages

**Spec:** Addendum "Dropbox upload for Partner Packages" in `docs/superpowers/specs/2026-10-13-ribbon-manufacturing-packages-design.md` (exact values there are binding: scopes, redirect `http://localhost:53682/`, token file path, DPAPI CurrentUser, folder `/Suppliers/<Partner>/`, 150 MB / 8 MB chunks, overwrite mode, viewer link, team path-root header).

**Files:**
- Create: `inventor-addin/SFVaultBridge.Core/Dropbox.cs` (`DropboxPaths`, `Pkce`, `DropboxJson` builders/parsers, `UploadPlan`), tests `inventor-addin/SFVaultBridge.Core.Tests/DropboxTests.cs`.
- Create: `inventor-addin/SFVaultBridge.Addin/DropboxClient.cs`, `DropboxAuth.cs`.
- Modify: `BridgeSettings` (Core) for `dropboxAppKey` (string, default ""), `PartnerPackageForm.cs` (checkbox), `PackageBuilder.cs` (upload step after the Downloads copy; result gains `DropboxPath`, `ShareUrl`, warnings), `RibbonActions.cs` (Sign out of Dropbox reachable from the Partner Package result or form), `inventor-addin/README.md` (app creation steps, sign-in, sign-out).

**Interfaces:**
- `DropboxPaths.PartnerFolder(string partner) -> string` (`/Suppliers/<clean partner>`), `DropboxPaths.FilePath(string partner, string zipName) -> string`.
- `Pkce.NewVerifier() -> string` (43-128 chars, unreserved set), `Pkce.Challenge(string verifier) -> string` (base64url SHA-256, no padding).
- `DropboxJson.UploadArg(string path) -> string` (`{"path":...,"mode":"overwrite","autorename":false,"mute":true}`), `DropboxJson.PathRoot(string rootNs) -> string`, `DropboxJson.SharedLinkArgs(string path, string visibility) -> string`, `DropboxJson.TryExistingLinkUrl(string errorJson, out string url) -> bool`, `DropboxJson.NeedsPathRoot(string accountJson, out string rootNs) -> bool`.
- `UploadPlan.Chunks(long length, long single = 150_000_000, int chunk = 8_388_608) -> IReadOnlyList<(long Offset, int Length)>` (empty list means single upload).

- [ ] Core: write failing tests (partner name with illegal Dropbox chars, PKCE challenge against the RFC 7636 Appendix B vector (verifier `dBjftJeZ4CVP-mJ92K9DqTVxeyFbVwP8i6tWbBqkpsQ` -> challenge `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuJGXUX5c0c`), upload arg JSON, path-root detection with equal and different namespace ids, existing-link parsing, chunk plan at 0, 150 MB, 150 MB + 1, 20 MB), implement, pass.
- [ ] Add-in: DropboxAuth (PKCE, browser via `Process.Start` with UseShellExecute, `HttpListener` on the redirect prefix with 3 min timeout, token exchange at `https://api.dropboxapi.com/oauth2/token`, DPAPI `ProtectedData` CurrentUser, refresh), DropboxClient (get_current_account, create_folder_v2 ignoring conflict, upload / upload sessions with `Dropbox-API-Arg`, shared links with public then team_only fallback). Never log tokens.
- [ ] Wire into the form and PackageBuilder per the addendum; clipboard on the UI thread.
- [ ] README; gates (Release build 0 errors, dotnet test green, pytest green); commit `Partner Package: Dropbox upload with share link`.
