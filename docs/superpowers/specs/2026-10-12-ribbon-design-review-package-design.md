# Design Review Package from the Inventor Ribbon: Design

**Date:** 2026-10-12
**Status:** Draft, awaiting Zak's review
**Related:** `2026-10-12-ribbon-purchasing-sheet-design.md` (shares the BOM reader, the Vault publisher and the Python tool)

## Goal

With an assembly open in Inventor, one click on the SF Vault Bridge ribbon
panel builds a single PDF containing everything needed to review the design,
and checks it into Vault next to the assembly. The PDF is also what Zak hands
to Claude for a design review.

## Decisions (Zak, 2026-10-12)

| # | Decision |
|---|---|
| 1 | Trigger is a ribbon button. |
| 2 | Output is **one combined PDF**. |
| 3 | Drawings are the **latest version in Vault**, including Work in Progress. |
| 4 | The PDF is checked into Vault in the assembly's folder; re-running adds a new version. |
| 5 | Every page is watermarked **DRAFT** so nobody mistakes a review copy for a released drawing. |

## Contents of the PDF, in order

1. **Cover page:** assembly name and Description, date and time, Vault user,
   assembly state and version, number of drawings included, and a **Missing
   drawings** list (every Make part or sub-assembly in the BOM with no
   drawing in Vault).
2. **BOM:** the structured BOM from the open assembly (same reader and
   ordering as the purchasing sheet): Item, Part Number, Description, QTY,
   BOM Structure, Material, REV, State. Repeats the header on each page.
3. **Drawings:** the top-level assembly's drawing first, then one entry per
   unique part or sub-assembly in BOM item order, all sheets of each drawing.
   Purchased and Content Center parts are skipped (Vault category Part -
   Purchased or Part - Content Center, or BOM Structure Purchased).
4. **DRAFT watermark** on every page (cover, BOM and drawings): large diagonal "DRAFT", red, about 25% opacity, applied after the merge with the existing `pdf_watermark.apply_watermark` so the drawings stay readable.
5. **Bookmarks:** Cover, BOM, then one per drawing (`<part number> - <drawing file name>`; hyphen, not a dash character).

File name: `<assembly name> Design Review.pdf`, category Documents.

## Finding and exporting drawings

- For each included component, ask Vault (Inventor's session) which files
  reference it ("where used"), keeping `.idw` and `.dwg` parents. If one has
  the component's file name stem, use only that one; otherwise use all of them.
  None: add to Missing drawings.
- Get the latest version of each drawing and its referenced models into the
  local Vault workspace with the session's file acquisition. Files checked out
  to the user are not downloaded over; the local copy is used.
- Open each drawing in Inventor invisibly and export all sheets to PDF with
  Inventor's PDF translator add-in (all sheets, vector, colors as in the
  drawing). Close it. Drawings that fail to open or export go to the cover
  page under **Could not export**, with the reason.
- `SilentOperation` is on for the whole run, as in the STEP import.

## Architecture

```
Ribbon button (add-in, UI thread)
  ├─ BomReader        (shared with the purchasing sheet)
  ├─ DrawingFinder    Vault where-used via the session -> drawing per component
  ├─ DrawingExporter  acquire latest, open invisibly, PDF translator -> temp PDFs
  ├─ SFVaultTools.exe review --bom <txt> --drawings <json> --cover <json> --out <pdf> --json
  │                   cover + BOM table (reportlab) + merge + bookmarks (pypdf) + DRAFT watermark (pdf_watermark.py)
  └─ VaultPublisher   (shared) add or new version, category Documents
```

The Python tool from the purchasing spec becomes `SFVaultTools.exe` with two
subcommands, `purchasing` and `review`. One exe, installed next to the add-in.

## Error handling

- Same guarantees as the rest of the add-in: nothing reaches Inventor; one
  message box; details in `bridge.log`.
- One drawing failing never stops the package; it is listed on the cover.
- If no drawing at all could be exported, the package is still built (cover +
  BOM) and the message says so.
- Temp PDFs are deleted after a successful merge.

## Testing

- Core: drawing choice rule (same-stem wins, else all, none = missing),
  skip rule for purchased and Content Center parts, bookmark labels, tool
  command line and JSON result parsing.
- Python: `review` builds a PDF from fixture inputs (small generated PDFs):
  page order, bookmark titles, missing list on the cover, DRAFT text present on every page, header row repeated
  on a multi-page BOM.
- Live, with Zak: CD-001887 open, click, PDF in its Vault folder with the
  assembly drawing first and CD-001888 (no Simplifyber drawing, purchased)
  skipped; a part without a drawing listed as missing; second click makes
  version 2.

## Out of scope (v1)

- Released-only mode.
- Markups, redlines or comments in the PDF.
- Attaching the PDF to the assembly (same reason as the purchasing sheet).
- Sending the package to Claude automatically; Zak opens or drags it in.
