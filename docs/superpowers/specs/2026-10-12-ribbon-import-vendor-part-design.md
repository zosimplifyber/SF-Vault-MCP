# Import New Part from the Inventor Ribbon: Design

**Date:** 2026-10-12
**Status:** Approved in chat by Zak (answers 2026-10-12 evening); built overnight, reviewed by Zak in the morning.
**Related:** `2026-10-09-mcmaster-intake-design.md` (the Claude-driven intake; this is the no-Claude ribbon version for any vendor), `2026-10-12-inventor-vault-bridge-design.md`.

## Goal

An **Import New Part** button on the SF Vault Bridge panel. Pick a vendor STEP and its PDF, confirm a short form, and the part lands in Vault numbered, filed under the vendor's Library folder, data card filled, PDF attached, category Part - Purchased. Works for any vendor, not only McMaster.

## Decisions (Zak, 2026-10-12)

| # | Decision |
|---|---|
| 1 | Build overnight; nothing installed and nothing written to Vault until Zak is back. |
| 2 | Vendor is a dropdown of the vendor folders under `$/DESIGNS/Simplifyber Library` (today McMaster-Carr, MiSUMi, Kiefel) plus **New vendor...**, which creates the folder. |
| 3 | Data card fields filled: Vendor, Vendor Number, Description, Material, Material Finish, Designer, Engr Approved By. Source = Buy and category Part - Purchased always. After the add, the runner reads Source, Vendor, Vendor Number, Material and Material Finish back from Vault and warns once per empty property. |
| 4 | The PDF is checked in beside the part and attached to it. |

## Flow

1. Click **Import New Part** (works with or without a document open).
2. One file dialog opens in Downloads, filter `*.stp;*.step;*.pdf`, multi-select. Zak selects the STEP and the PDF (exactly one of each; otherwise a message says what is missing and the dialog reopens or cancels).
3. The **Import New Part** form opens, prefilled:
   - **Vendor**: dropdown of Library vendor folders + New vendor... Preselected when the file name identifies the vendor (McMaster number pattern `^\d{3,5}[A-Z]\d{1,4}`), else the last vendor used.
   - **Subfolder**: dropdown of the chosen vendor folder's subfolders (one level, plus nested ones as `screws/shoulder screws`), `(vendor folder itself)`, and New subfolder...
   - **Vendor Number**: from the file name (McMaster: the leading part number; other vendors: the text before the first `_`, else empty), editable, required.
   - **Description**: from the file name (McMaster: the text after the first `_`, extension removed), editable, required.
   - **Comments**: optional multi-line text for extra description lines (McMaster's second line is on the web page, not in the PDF), written to the Comments iProperty.
   - **Material**: required pick-only dropdown of the active material library, preselected to Steel when present, else the first item. A material that cannot be applied at run time fails step part before anything is added for the part.
   - **Material Finish**: text, defaults to As Finished, editable; empty becomes As Finished.
   - **Designer**: Inventor's user name (General Options) in "First L." form ("Zak Olech" becomes "Zak O."), read-only.
   - **Engr Approved By**: `NOT REVIEWED`, read-only.
   - Shows where it will go: `$/DESIGNS/Simplifyber Library/<Vendor>/<Subfolder>/CD-<next>.ipt` and the PDF name.
4. **Import** runs, on Inventor's session, in this order, each step reported in the result:
   1. **Duplicate check**: a file in Vault with the same Vendor and Vendor Number (case- and space-insensitive) stops the import: "Already in Vault as CD-xxxxxx (<folder>)." Nothing written.
   2. **Number**: next number from the default file numbering scheme (CAD Data Autonumbering); must match `^CD-\d{6}$` and no file named `CD-xxxxxx.*` may exist (else stop, nothing else written).
   3. **Folders**: create New vendor / New subfolder if chosen.
   4. **Part**: open the STEP as a part (existing StepImporter rules: silent, Convert Model, no overwrite, single part only), set iProperties, set the Inventor material when one was picked and exists in the active library, save `<CD>.ipt` in the matching local workspace folder.
   5. **PDF**: copy to `<CD> <Vendor> <Vendor Number>.pdf` beside the part, check in, category Documents.
   6. **Add part** with the PDF attached; category Part - Purchased.
   7. **Read back** category and state.
   8. **Tidy**: move the two downloads into `Downloads\Imported\`.
   On a failure: stop, name the step, list what already reached Vault, undo nothing automatically (same rule as the Claude intake), and keep the downloads where they were.
5. Result message: CD number, Vault path, "PDF attached", and any warnings; button **Open part** opens the new .ipt.

## Data card mapping

| Field | Inventor iProperty | Value |
|---|---|---|
| Part Number | Design Tracking "Part Number" | CD number |
| Description | Design Tracking "Description" | form |
| Title | Summary "Title" | `<CD> - <Description>`, e.g. `CD-001909 - Black-Oxide Alloy Steel Socket Head Screw` (Zak, 2026-10-13); follows the CD number Vault applies on save |
| Vendor | Design Tracking "Vendor" | vendor folder name as shown (e.g. `McMaster-Carr`); for McMaster the existing parts use `McMASTER-CARR`, so McMaster-Carr maps to `McMASTER-CARR` to keep one spelling |
| Vendor Number | custom "Vendor Number" | form |
| WEB Link | Design Tracking "Catalog Web Link" | Vendor Number (matches existing parts) |
| Source | custom "Source" | `Buy` |
| Material | part material | form (required) |
| Material Finish | custom "Material Finish" | form |
| Comments | Summary "Comments" | form, optional multi-line (e.g. McMaster's second description line); lines trimmed, blanks dropped |
| Designer | Design Tracking "Designer" | Inventor user name as "First L." (Zak, 2026-10-13) |
| Engr Approved By | custom "Engr Approved By" | `NOT REVIEWED` |

## Architecture

```
Ribbon button -> ImportPartForm (WinForms, owned by Inventor's window)

Form layout: DPI-safe. AutoScaleMode Dpi, a two-column TableLayoutPanel (labels AutoSize, inputs a fixed logical width), form AutoSize GrowAndShrink with no fixed ClientSize, buttons in an AutoSize right-aligned FlowLayoutPanel, Files in a read-only multiline TextBox. All rows (Files through the error line and buttons) stay visible at 100% and 150%.
              -> ImportPartRunner (add-in, UI thread)
                   VaultFiles (+ new: LibraryVendors, Subfolders, FindByVendorNumber, GenerateNumber, AddFolder)
                   StepImporter (+ material)
              Core (unit-tested): VendorFileNames (prefill rules), ImportPlan (validation, paths, names, vendor spelling), DuplicateKey
```

## Testing

- Core: file-name prefill per vendor (McMaster names with and without description, MiSUMi-style names, unknown names); plan validation (required fields, illegal folder characters, new vendor/subfolder names); Vault path and file names; vendor spelling map; duplicate key normalisation.
- Add-in: build-verified; live run with Zak on one McMaster and one MiSUMi part.

## Out of scope

- Spec cards (the Claude intake writes them; the ribbon import has no PDF reader). Claude can add a card later.
- Batch import of several parts at once.
- Vault items.
