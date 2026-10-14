# Assembly Package from the Inventor Ribbon: Design

**Date:** 2026-10-13
**Status:** Approved in chat by Zak (answers 2026-10-12 evening); built overnight, reviewed by Zak in the morning.
**Related:** `2026-10-12-ribbon-design-review-package-design.md` (shares the BOM reader, drawing finder/exporter, Python PDF tool and Vault publisher).

## Goal

An **Assembly Package** button that builds the PDF assemblers work from and checks it into Vault next to the assembly, where they open it in the Vault web portal.

## Decisions (Zak, 2026-10-12)

| # | Decision |
|---|---|
| 1 | Contents: assembly drawing + sub-assembly drawings + BOM. No part detail drawings, no vendor PDFs. |
| 2 | Latest version of each drawing; pages from drawings that are not in the Released state carry a **NOT RELEASED** stamp. |
| 3 | A small **FOR ASSEMBLY** footer on every page (assembly number, revision, date), no watermark otherwise. Checked into the assembly's Vault folder as `<assembly> Assembly Package.pdf`. |

## Contents, in order

1. **Cover:** assembly name and Description, Vault revision and state, date, Vault user, the list of included drawings with their state, and **Missing drawings** (sub-assemblies with no drawing) and **Could not export** lists. No vendor section.
2. **BOM:** same table as the design review (same reader, ordering, columns).
3. **Drawings:** the top assembly's drawing first, then one per unique sub-assembly (BOM rows whose file is `.iam`) in BOM item order. Skipped: Phantom, Reference, purchased and Content Center rows (same rules as the review). Drawing choice: same rule as the review (same-stem `.idw`, else same-stem `.dwg`, else name-matched/where-used drawings).
4. **Stamps:** pages from a drawing whose Vault state is not `Released` get a diagonal red **NOT RELEASED** stamp (same style as DRAFT). Released drawings get none.
5. **Footer on every page:** `FOR ASSEMBLY | <assembly> Rev <rev> | <yyyy-MM-dd>` in small grey text at the bottom margin (ASCII separators, no dash characters). `<rev>` is the assembly's Vault revision, or `-` when it has none.
6. **Bookmarks:** Cover, BOM, then `<part number> - <drawing file name>`.

File name `<assembly file stem> Assembly Package.pdf`, category Documents, new version on rerun, same publish rules and safety fixes as the other buttons (temp output, checked-out files block, failed publish keeps a copy).

## Architecture

- Ribbon: fifth large button **Assembly Package** (icon: clipboard with a check mark, same flat style), assembly required.
- Add-in: the review flow is generalised into a package builder with a mode (`Review` or `Assembly`) that controls which components are included, whether vendor PDFs are collected, and the output name; drawing finding/export, BOM, manifest and publish are shared.
- Manifest: each drawing entry gains `"state"` (Vault lifecycle state name); top level gains `"mode"` (`review` or `assembly`), `"revision"`, and for assembly mode no `purchased`/`no_vendor_drawing` lists.
- Python `review_package.run_review` takes the mode from the manifest: `review` keeps today's DRAFT behaviour exactly; `assembly` applies NOT RELEASED per drawing, the footer on every page, no DRAFT, and the cover variant. CLI unchanged (`review` subcommand reads the mode).

## Testing

- Core: assembly inclusion rule (sub-assembly rows only, skips), manifest fields (mode, revision, state), output name.
- Python: assembly mode: no DRAFT anywhere, NOT RELEASED only on pages of unreleased drawings, footer text on every page with the right assembly/rev/date, cover lists; review mode unchanged (existing tests stay green).
- Live with Zak: CD-001887 (Work in Progress drawings expected, so stamps on all drawing pages), check footer and that part drawings are absent.

## Out of scope

- Released-only mode, markups, SharePoint copy, attaching to the assembly.
