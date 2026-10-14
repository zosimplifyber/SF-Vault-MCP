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
| 6 | Three equal large buttons on the panel (Vault Bridge, Purchasing Sheet, Design Review) with icons drawn in code (round 2). |
| 7 | Drawings are also matched by file name; purchased parts contribute their vendor PDF (round 2). |

## Contents of the PDF, in order

1. **Cover page:** assembly name and Description, date and time, Vault user,
   assembly state and version, number of drawings included, a **Missing
   drawings** list (every Make part or sub-assembly in the BOM with no
   drawing in Vault), a **Could not export** list and a **No vendor drawing**
   list (purchased parts with no vendor PDF in Vault; never counted as missing).
2. **BOM:** the structured BOM from the open assembly (same reader and
   ordering as the purchasing sheet): Item, Part Number, Description, QTY,
   BOM Structure, Material, REV, State. Repeats the header on each page.
3. **Drawings:** the top-level assembly's drawing first, then one entry per
   unique part or sub-assembly in BOM item order, all sheets of each drawing.
   Classification: Phantom and Reference rows and Content Center parts (Vault
   category Part - Content Center, or a path under `Content Center Files`) are
   ignored everywhere. Purchased = BOM Structure Purchased or Vault category
   Part - Purchased. Everything else is a Simplifyber part or sub-assembly.
4. **Purchased parts:** after the drawings, one section per unique purchased
   part, in manifest order: its vendor PDF, used as downloaded (no Inventor
   export). Bookmark `Purchased: <part number> (<vendor short> <vendor number>)`;
   vendor short is `McMaster` when the vendor contains MCMASTER (any case),
   otherwise the vendor text; the parenthesis is omitted when both are empty.
   Vendor and Vendor Number come from the part's iProperties (Design Tracking
   "Vendor", user-defined "Vendor Number"). The PDF is the first `.pdf`
   attached to the part's latest Vault version (the McMaster intake attaches
   `<CD> McMaster <pn>.pdf`), else the first `.pdf` in Vault whose name starts
   with the part's file stem or contains its Vendor Number. An unreadable PDF
   goes to Could not export.
5. **DRAFT watermark** on every page (cover, BOM and drawings): large diagonal "DRAFT", red, about 25% opacity, applied after the merge with the existing `pdf_watermark.apply_watermark` so the drawings stay readable.
6. **Bookmarks:** Cover, BOM, one per drawing (`<part number> - <drawing file name>`; hyphen, not a dash character), then one per purchased part.

File name: `<assembly name> Design Review.pdf`, category Documents.

## Finding and exporting drawings

- For each included component, ask Vault (Inventor's session) which files
  reference it ("where used"), keeping `.idw` and `.dwg` parents, and also
  search Vault by file name for any `.idw`/`.dwg` (latest versions, all
  folders) whose file name stem equals the component's stem. Merge and
  de-duplicate by master id. If one has
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

## Ribbon look

Three equal large buttons. Icons are drawn in code at 16 and 32 px on a
transparent background: Purchasing Sheet is a spreadsheet grid with a green
(#2E9E44) header row; Design Review is a page with a folded top-right corner
and a red (#C62828) diagonal band reading DRAFT (band only at 16 px). Labels
are left to Inventor's own wrapping.

## Error handling

- If publishing to Vault fails for any reason, the finished file is copied to
  `Downloads\SF Vault Bridge\<name>` (` (n)` added if taken) before the temp
  folder is deleted, and the message names that path.
- Only the check-in itself undoes a checkout. Failing to read the version back
  after a successful check-in or add is a warning, not a failed check-in.
- Failing to write the new file into the workspace names the local path.

- Same guarantees as the rest of the add-in: nothing reaches Inventor; one
  message box; details in `bridge.log`.
- One drawing failing never stops the package; it is listed on the cover.
- If no drawing at all could be exported, the package is still built (cover +
  BOM) and the message says so.
- Temp PDFs are deleted after a successful merge.

## Testing

- Core: drawing choice rule (same-stem wins, else all, none = missing),
  classification (ignored / purchased / make), vendor PDF name rules, saved-copy
  naming, manifest JSON with the purchased and no_vendor_drawing lists, bookmark labels, tool
  command line and JSON result parsing.
- Python: `review` builds a PDF from fixture inputs (small generated PDFs):
  page order, bookmark titles, purchased section order and bookmark text,
  missing and no-vendor lists on the cover, DRAFT text present on every page, header row repeated
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
