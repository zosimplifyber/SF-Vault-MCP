# Assembly Package (ribbon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A fifth ribbon button, Assembly Package, that builds `<assembly> Assembly Package.pdf` (cover, BOM, assembly + sub-assembly drawings, NOT RELEASED stamps on unreleased drawings, FOR ASSEMBLY footer) and checks it into Vault.

**Architecture:** Extend the existing design-review pipeline with a mode. Python `review_package.run_review` reads `mode` from the manifest; Core gains the assembly inclusion rule and manifest fields; the add-in's review flow becomes a package builder parameterised by mode, with a new button.

**Tech Stack:** Python 3.14 (pypdf, reportlab), C# .NET 8, xUnit, Inventor 2026, Vault 2026.

**Spec:** `docs/superpowers/specs/2026-10-13-ribbon-assembly-package-design.md`

## Global Constraints

- Review mode output must stay byte-for-byte equivalent in behaviour: DRAFT on every page, vendor section, existing cover and bookmarks; all existing review tests stay green unchanged.
- Assembly mode: no DRAFT; diagonal red `NOT RELEASED` (same colour/opacity as DRAFT: `#C62828`, 0.25) only on pages of drawings whose `state` is not `Released` (case-insensitive); footer `FOR ASSEMBLY | <assembly> Rev <rev> | <yyyy-MM-dd>` on every page (ASCII `|`, rev `-` when empty), small grey text in the bottom margin; cover lists included drawings with state, Missing drawings, Could not export; no vendor section.
- Included drawings in assembly mode: top assembly drawing first, then one per unique `.iam` BOM row in item order, skipping Phantom, Reference, purchased and Content Center rows; same drawing choice and dedupe as the review.
- Output `<stem> Assembly Package.pdf`, Documents, new version on rerun, same publish/safety behaviour as the other buttons.
- Fifth large button `Assembly Package`, icon clipboard with check mark (flat, transparent, 16/32), assembly required (existing Click guard).
- No em dashes; never run the installer, start/automate Inventor, or connect to Vault while building; rebuild `inventor-addin\tools\SFVaultTools.exe` after the Python change (delete it, run `cmd //c ".\build_vault_tools_exe.bat"`).

## Review Focus

1. A drawing with state `released` (lower case) or `Released ` (trailing space) is treated as released. Test in Task 1.
2. A multi-sheet unreleased drawing gets NOT RELEASED on every one of its pages, and the next released drawing's pages get none. Test in Task 1.
3. An assembly with no sub-assemblies produces cover + BOM + assembly drawing only. Test in Task 2 (rule) and Task 1 (PDF).
4. Footer text never overlaps the drawing content badly enough to hide the title block: placed at 0.25 in from the bottom, 7 pt grey. Visual live check.
5. Manifest without `mode` (older add-in) still builds a review package. Test in Task 1.

---

### Task 1: Python assembly mode

**Files:** Modify `review_package.py`; Test `tests/test_review_package.py` (add assembly tests; keep existing ones unchanged).

- Manifest additions: top-level `mode` (`"review"` default when missing, or `"assembly"`), `revision` (string, may be empty); each drawing `state` (string, may be empty).
- `run_review` in assembly mode: cover variant (title `Assembly package: <assembly>`, `Revision <rev or ->`, list of included drawings `<part> - <drawing> (<state or unknown>)`, Missing drawings, Could not export; no No vendor drawing section); BOM unchanged; drawings in manifest order; skip `purchased` entirely.
- Stamping: build the merged PDF, then stamp per page: in review mode `apply_watermark(..., "DRAFT", ...)` as today; in assembly mode, for each page index belonging to an unreleased drawing overlay `NOT RELEASED` (reuse `pdf_watermark._make_watermark_page`/`apply_watermark` logic per page, or add a small public helper in pdf_watermark that stamps selected page indices; keep `apply_watermark` signature unchanged), then overlay the footer on every page (reportlab canvas, Helvetica 7 pt, grey `#666666`, centred, 18 pt above the bottom edge of each page's mediabox). Bookmarks added last, as today.
- Result adds `"mode"` and `"unreleased": <number of drawings stamped>`.
- Tests: assembly mode with one released 1-page drawing and one unreleased 2-page drawing -> pages of the unreleased drawing contain NOT RELEASED, released pages and cover/BOM do not; no page contains DRAFT; every page contains `FOR ASSEMBLY | CD-001887 Rev 3 |` and today's date; empty revision renders `Rev -`; state `released ` counts as released; manifest without `mode` behaves as review (DRAFT present); assembly mode ignores `purchased`.
- Commit `Review package: assembly mode with NOT RELEASED stamps and FOR ASSEMBLY footer`; then rebuild the exe and smoke-test both modes (show both result JSONs in the report).

### Task 2: Core assembly rules and manifest fields

**Files:** Modify `inventor-addin/SFVaultBridge.Core/Outputs.cs` (+ tests).

- `enum PackageMode { Review, Assembly }`.
- `ReviewRules.IncludeInAssemblyPackage(BomNode node, string? vaultCategory) -> bool`: true only when `node.FileName` ends with `.iam` (case-insensitive) and the node is not ignored (Phantom/Reference/Content Center) and not purchased (same classification helpers as the review).
- `VaultPaths.AssemblyPackageName(string assemblyFile)` -> `<stem> Assembly Package.pdf`.
- `ReviewDrawing` gains optional trailing `string State = ""`; `ReviewManifest.ToJson` gains optional `PackageMode mode = PackageMode.Review` and `string revision = ""` parameters written as `mode` (`review`/`assembly`) and `revision`; each drawing writes `state`. Existing calls compile unchanged; existing manifest test still passes.
- Tests for each rule branch, the name, and the JSON fields.
- Commit `Bridge core: assembly package rules, name, manifest mode/revision/state`.

### Task 3: Add-in package builder mode, button, icon, README

**Files:** Modify `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (generalise `Review` into `BuildPackage(asm, PackageMode mode)`; Review button calls it with Review; new Assembly Package button calls it with Assembly), `Icons.cs` (clipboard + check icon), `VaultFiles.cs` only if a state lookup helper is needed (drawing file's `FileLfCyc.LfCycStateName`; assembly revision from the assembly file's revision property `FileRev.Label` or equivalent, confirm by compiling), `inventor-addin/README.md` (Assembly Package section).
- In Assembly mode: include the top assembly drawing plus `IncludeInAssemblyPackage` rows; do not collect vendor PDFs; record each exported drawing's Vault state in `ReviewDrawing.State`; manifest mode Assembly with the assembly's revision; output name `AssemblyPackageName`; summary line `<pages> pages, <drawings> drawings, <unreleased> not released, <missing> missing, <failed> could not export.`
- Review mode behaviour unchanged (same outputs and messages as before).
- Gates: Release build 0 errors; `dotnet test inventor-addin/SFVaultBridge.sln` green; `py -3.14 -m pytest -q` green. Commit `Ribbon: Assembly Package button`.
