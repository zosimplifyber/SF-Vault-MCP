# Manufacturing Packages and Process Property (ribbon): Design

**Date:** 2026-10-13
**Status:** Approved in chat by Zak 2026-10-13 ("ok, go ahead and start building").
**Related:** `2026-10-12-ribbon-design-review-package-design.md`, `2026-10-13-ribbon-assembly-package-design.md`, `2026-10-12-ribbon-purchasing-sheet-design.md`, `2026-10-12-ribbon-import-vendor-part-design.md`.

## Goal

1. Every BOM the add-in produces shows whether each row is released.
2. A **Partner Package** button builds a zip of models, drawings and a BOM for a manufacturing partner, in three flavours: All Make parts, CNC, Sheet metal.
3. A **3D Print Package** button builds a zip of print files for internally printed parts.
4. A **Process** iProperty (Inventor custom property, mapped to a Vault UDP by Zak) drives which parts go into which package.

## Decisions (Zak, 2026-10-13)

| # | Decision |
|---|---|
| 1 | Released or not: a **State** column in every BOM (Purchasing Sheet already has one; add to the Design Review, Assembly Package, Partner and 3D Print BOMs) and rows whose state is not `Released` (case-insensitive, trimmed) highlighted light yellow (`#FFF59D`) in every BOM (xlsx fill and PDF table row background). |
| 2 | Partner Package parts: Make parts only (same Source rule as the Purchasing Sheet: Vault category Part - Purchased / Part - Content Center = Buy; BOM Structure Purchased = Buy; Phantom and Reference skipped; Content Center skipped). Make sub-assemblies are included. The top assembly itself is included (STEP + drawing) in All Make parts mode. |
| 3 | Partner Package contents: STEP per part, PDF drawing per part, DXF flat pattern for sheet metal parts, BOM.xlsx, cover PDF; zipped into Vault next to the assembly plus a copy in Downloads. |
| 4 | Partner is named by typing in a form; the form remembers recent names. |
| 5 | CNC work goes to partners: the Partner Package form has a **Parts** choice: All Make parts / CNC (machined) / Sheet metal. CNC files per part: STEP + PDF drawing. CAM software does not matter. |
| 6 | Parts are selected by the **Process** iProperty when set, else by fallback rules (below). Zak creates the Vault UDP and mapping by hand (steps in README). |
| 7 | 3D Print Package: STL/3MF per part, selected by Process = 3D Print, else by print material. |

## Process property

- Inventor custom iProperty `Process` (User Defined Properties). Values (settings file, editable): `CNC`, `3D Print`, `Sheet Metal`, `Weldment`, `In-house`, `Other`. Matching is case-insensitive and trimmed.
- Vault: UDP `Process` (Text, list values as above), assigned to Part - Engineering and Assembly - Engineering, mapped bidirectionally to the Inventor custom property with Create. Zak does this in Vault Settings; README documents the steps. The add-in only reads/writes the Inventor iProperty.
- `fix_data_card` already sets custom iProperties (`--custom Process=CNC`); no new write path is needed. README example added.
- BomReader reads `Process` per row (custom iProperty of the row's component definition document; blank when missing). BomNode gains trailing optional `Process`. BomFile gains a `Process` column after `Material`.

## Part selection rules (Core, pure, tested)

`PackageRules.Classify(BomNode node, string? vaultCategory, PrintMaterials materials, bool isSheetMetal) -> PackageKind` where `PackageKind` is `None` (not a Make part, or ignored) / `Cnc` / `SheetMetal` / `Print` / `OtherMake`, plus a `Guessed` flag when Process was blank.

1. Not Make (Buy per the Source rule), Phantom, Reference, Content Center, virtual components -> `None`.
2. Process set: `CNC` -> Cnc; `Sheet Metal` -> SheetMetal; `3D Print` -> Print; any other value -> OtherMake. Not guessed.
3. Process blank (guessed): sheet metal part (document has a SheetMetalComponentDefinition) -> SheetMetal; material in the print list -> Print; `.ipt` otherwise -> Cnc; `.iam` -> OtherMake.

Mode inclusion:
- **All Make parts**: every row with kind != None, plus the top assembly. Files: STEP for every part and Make sub-assembly, PDF drawing where one exists, DXF for SheetMetal parts.
- **CNC**: kind Cnc. Files: STEP + PDF drawing.
- **Sheet metal**: kind SheetMetal. Files: DXF flat pattern + PDF drawing.
- **3D Print**: kind Print. Files: 3MF (STL fallback).

Print materials (settings file, editable; match is "material display name contains any entry", case-insensitive): `PLA`, `PETG`, `ABS`, `ASA`, `PA11`, `PA12`, `Nylon`, `TPU`, `PC`, `Resin`, `CF`. Note `PC` and `CF` are short; match on word boundary (`\bPC\b`) to avoid false hits such as "PCB".

## Settings file

`%APPDATA%\Simplifyber\VaultBridge\settings.json`, created with defaults on first use if missing, read on each run:
```json
{ "processValues": ["CNC", "3D Print", "Sheet Metal", "Weldment", "In-house", "Other"],
  "printMaterials": ["PLA", "PETG", "ABS", "ASA", "PA11", "PA12", "Nylon", "TPU", "PC", "Resin", "CF"],
  "recentPartners": [] }
```
`recentPartners` keeps the last 10 names, most recent first, case-insensitive dedupe. Malformed file: log, use defaults, do not overwrite it.

## Partner Package button

1. Large button `Partner Package` (icon: box with outgoing arrow, flat, transparent 16/32), assembly required (existing Click guard).
2. Form (DPI-safe, same layout pattern as ImportPartForm): **Partner** (editable combo of recent partners, required, file-name-safe characters), **Parts** (All Make parts / CNC (machined) / Sheet metal, default All Make parts), a live preview line "N parts: x STEP, y PDF, z DXF" computed from the BOM classification, **Build** / **Cancel**.
3. Build, in order, each step reported:
   1. Read BOM (existing reader, structured, with Process), enrich with Vault category/state/revision (existing BomEnricher), classify.
   2. For each included part, latest Vault version: download without children into a temp folder only if not already the latest locally (existing AcquireLatest rules); open invisibly (reuse an open document without closing it); export:
      - STEP: Inventor STEP translator, AP214, `<CD> Rev <rev>.stp` (rev `-` when empty). For an assembly, its children must be resolvable: use the user's workspace (the assembly is open, children are local).
      - DXF: sheet metal flat pattern via `DataIO.WriteDataToFile("FLAT PATTERN DXF?AcadVersion=2018&OuterProfileLayer=Outer", path)`; no flat pattern -> listed on the cover as "No flat pattern"; `<CD> Rev <rev>.dxf`.
      - PDF drawing: same drawing choice as the Assembly Package (where-used drawing, name match), same NOT RELEASED stamp rule (not Released, or open/checked out), `<CD> Rev <rev>.pdf`. Missing drawing -> cover list.
   3. BOM.xlsx: rows of included parts (Item, Part Number, Description, Qty (total in assembly), Material, Material Finish, Process (value or "guessed: <kind>"), REV, State, File(s) in package), unreleased rows yellow, title row `<assembly> <Partner> <mode> package`, row 2 `Generated: <date> | <source line>` (the existing BomSource line).
   4. Cover PDF `00 Cover.pdf`: title, partner, mode, assembly + rev + state, counts, lists: Process not set (guessed), Missing drawings, No flat pattern, Could not export (with reason). FOR <PARTNER> footer not required.
   5. Zip: `<assembly> <Partner> <Mode> Package.zip` where Mode is `Make`, `CNC` or `Sheet Metal`; folders `STEP/`, `PDF/`, `DXF/` (omit empty), BOM.xlsx and `00 Cover.pdf` at the root.
   6. Publish: check into Vault next to the assembly, category Documents, new version on rerun, same safety as the other buttons (any checkout blocks, temp output, failure keeps a copy in `Downloads\SF Vault Bridge`). Always also copy the zip to `Downloads\SF Vault Bridge\`.
   7. Remember the partner in settings.json.
4. Result message: zip name, Vault path, counts, warnings; button **Open folder** opens the Downloads copy's folder.

## 3D Print Package button

1. Large button `3D Print Package` (icon: printer nozzle over a layered block), assembly required.
2. No form. Classify; kind Print only.
3. Per part: open as above, export 3MF via Inventor's 3MF translator if present (find the translator add-in by its display name containing "3MF"; confirm the ClassId at build time by enumerating `ApplicationAddIns` in code, never hard-code an unverified id); else STL via the STL translator (`{533E9A98-FC3B-11D4-8E7E-0010B541CD80}`, binary, high resolution). Name `<CD> Rev <rev> x<qty>.3mf` (or `.stl`), folder per material (file-name-safe material name).
4. `Print List.xlsx`: Part Number, Description, Qty, Material, Process (value or guessed), REV, State, File; unreleased rows yellow; row 2 source line.
5. Zip `<assembly> 3D Print Package.zip`, publish like the Partner Package, copy to Downloads.
6. Nothing to print -> message "No 3D print parts found (Process = 3D Print or a print material)." and nothing written.

## State column and yellow rows in existing outputs

- Design Review and Assembly Package BOM tables (review_package.py): add `State` after `REV` (the BOM file already carries `State`), and give rows whose state is not Released a `#FFF59D` background. Review mode output otherwise unchanged.
- Purchasing Sheet (bom_purchasing.py, Purchasing sheet and By Vendor if it has State): yellow fill on rows whose State is not Released. Rows with blank State (not in Vault) are also yellow. MCP-path output must stay valid; the fill is the only change there.

## Architecture

- Core (tested): `PackageRules` (classification, inclusion, file names, zip name), `PrintMaterials` (word-boundary match), `BridgeSettings` (load/save/defaults/recent partners), `PackageManifest` JSON writer, `BomNode.Process`, `BomFile` Process column.
- Add-in: `PackageBuilder` (shared export loop for Partner and 3D Print), `PartnerPackageForm`, `ModelExporter` (STEP, DXF, 3MF/STL), RibbonActions (two new buttons), Icons, BomReader (Process + sheet-metal flag).
- Python (SFVaultTools.exe, new subcommand `package`): builds BOM.xlsx / Print List.xlsx and `00 Cover.pdf` from a manifest the add-in writes, and zips the folder (`zipfile`, deflate). Reuse review_package's cover styling and bom_purchasing's xlsx styling. `--result` JSON like the other subcommands. The installer's stale-exe rule already rebuilds the exe when these Python files change; add any new Python file to that list.

## Testing

- Core: every classification branch (Process values, blanks with sheet metal / print material / ipt / iam, Buy, Phantom, Reference, Content Center, virtual), mode inclusion, PC/CF word-boundary, file and zip names (illegal characters in partner names), settings defaults, malformed file, recent-partner ordering and cap.
- Python: package subcommand builds xlsx with yellow unreleased rows and State column, cover lists, zip layout with empty folders omitted; review_package State column and yellow rows; bom_purchasing yellow rows; manifest without new fields still works.
- Add-in: build-verified; live run with Zak on CD-001882 (Partner Package in each mode, 3D Print Package).

## Out of scope

- Shop traveler, CNC in-house package, per-partner part selection, Parasolid/native exports, emailing the zip.
- Creating the Vault UDP (Zak does it; README steps).
