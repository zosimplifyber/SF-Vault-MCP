# Ribbon Purchasing Sheet and Design Review Package Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two buttons on the SF Vault Bridge ribbon panel. **Purchasing Sheet** turns the open assembly's BOM into the Simplifyber purchasing workbook and checks it into Vault. **Design Review** builds one DRAFT-watermarked PDF (cover, BOM, latest drawings) and checks it into Vault.

**Architecture:** The add-in reads the BOM and drawings inside Inventor, writes a tab-delimited BOM file (and, for review, a JSON manifest plus one PDF per drawing), and runs `SFVaultTools.exe` (a PyInstaller build of a new `vault_tools_cli.py` that reuses `bom_purchasing` and `pdf_watermark`). The tool writes a result JSON file; the add-in reads it and checks the output into the assembly's Vault folder on Inventor's session. Pure logic (ordering, file format, path mapping, drawing choice, argument building, result parsing) lives in `SFVaultBridge.Core` and is unit-tested.

**Tech Stack:** C# .NET 8 (existing add-in), xUnit; Python 3.14, pandas/openpyxl (existing engine), pypdf + reportlab, pytest; PyInstaller.

**Specs:** `docs/superpowers/specs/2026-10-12-ribbon-purchasing-sheet-design.md`, `docs/superpowers/specs/2026-10-12-ribbon-design-review-package-design.md`

## Global Constraints

- Buttons live on the existing **SF Vault Bridge** panel (Tools tab) next to the status button: **Purchasing Sheet** and **Design Review**. Outside an assembly they show "Open an assembly first."
- BOM: Structured view, all levels, read from the open document (unsaved edits included). Siblings sorted by Description descending (case-insensitive), ties by Part Number ascending, renumbered `1`, `1.1`, `1.2`, `2`. Part Number falls back to the file name stem.
- BOM file headers exactly: `Item`, `Part Number`, `BOM Structure`, `Unit QTY`, `QTY`, `Description`, `REV`, `Material`, `File Name` (tab-delimited, UTF-8, CRLF).
- Output names: `<assembly file stem> Purchasing.xlsx` and `<assembly file stem> Design Review.pdf`, saved in the assembly's local workspace folder, checked into the assembly's Vault folder, category **Documents**. Existing file: check out, replace, check in as a new version. Checked out by someone else: stop with "<user> has <file> checked out."
- Review PDF order: cover, BOM table (header row repeated), assembly drawing first, then one drawing per unique included component in BOM item order, all sheets. Purchased parts (BOM Structure Purchased, or Vault category Part - Purchased / Part - Content Center, or under `Content Center Files`) are skipped. Drawing choice: same-stem `.idw` first, else same-stem `.dwg`, else every `.idw`/`.dwg` that references the component; none = cover "Missing drawings" list. Export failures = cover "Could not export" list.
- Drawings are the latest version in Vault; files checked out to the user are never downloaded over.
- Every page of the review PDF carries a diagonal **DRAFT** watermark, red `#C62828`, opacity 0.25. Bookmarks: `Cover`, `BOM`, then `<part number> - <drawing file name>` (ASCII hyphen).
- `SFVaultTools.exe` is windowed (no console flash), takes `--result <json path>` and writes its result there (stdout is unavailable in a windowed exe). Result: `{"ok": true, ...}` or `{"ok": false, "error": "<plain message>"}`. It needs no Vault password or config.json.
- Add-in guarantees stay: nothing reaches Inventor, one message box per click, details in `bridge.log`. `SilentOperation` on during drawing export. A click while a run is in progress is ignored with "Already running."
- Python `py -3.14`; C# `export PATH="/c/Program Files/dotnet:$PATH"`. No em dashes in any user-facing string.

## Review Focus

1. **Description with a tab, newline or double quote** (`1/2" bolt`): the BOM file still parses to the same rows in pandas. Test in Task 4 (writer) and Task 1 (engine reads a quoted field).
2. **Assembly not under the Vault workspace** (new file never checked in, or opened from Desktop): stops before any build with "Check the assembly in to Vault first." Test in Task 4 (`VaultPaths`).
3. **Tool never writes a result** (crash, killed, timeout): the message names the tool and the log, not a raw exception. Test in Task 4 (`ToolResults.Parse(null)`), Task 5 wiring.
4. **A drawing that fails to export** must not stop the package; it appears under "Could not export". Test in Task 2 (cover lists failures) and Task 6 wiring.
5. **Long BOM (80+ rows)** in the review PDF: header row repeats on every BOM page and every page still carries DRAFT. Test in Task 2.

---

## File Structure

| File | Responsibility |
|---|---|
| `vault_tools_cli.py` (create) | CLI entry for SFVaultTools.exe: `purchasing` and `review` subcommands, result JSON. |
| `review_package.py` (create) | Cover page, BOM table, merge, DRAFT watermark, bookmarks. |
| `build_vault_tools_exe.bat` (create) | PyInstaller build to `inventor-addin\tools\SFVaultTools.exe`. |
| `tests/test_vault_tools_cli.py`, `tests/test_review_package.py` (create) | Python tests. |
| `inventor-addin/SFVaultBridge.Core/Bom.cs` (create) | `BomNode`, `BomLine`, `BomOrdering`, `BomFile`. |
| `inventor-addin/SFVaultBridge.Core/Outputs.cs` (create) | `VaultPaths`, `ReviewRules`, `ReviewManifest`, `ToolResults`. |
| `inventor-addin/SFVaultBridge.Core.Tests/BomTests.cs`, `OutputsTests.cs` (create) | Core tests. |
| `inventor-addin/SFVaultBridge.Addin/BomReader.cs` (create) | Inventor BOM to `BomNode` tree. |
| `inventor-addin/SFVaultBridge.Addin/VaultFiles.cs` (create) | Working folder, find latest, publish (add or new version), category, where-used drawings, acquire latest. |
| `inventor-addin/SFVaultBridge.Addin/ToolRunner.cs` (create) | Run SFVaultTools.exe, read result file. |
| `inventor-addin/SFVaultBridge.Addin/DrawingExporter.cs` (create) | Open drawing invisibly, PDF translator, close. |
| `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (create) | The two buttons and their click flows. |
| `inventor-addin/SFVaultBridge.Addin/RibbonStatus.cs`, `AddInServer.cs`, `VaultSession.cs` (modify) | Expose panels and the connection; create RibbonActions. |
| `inventor-addin/Install-VaultBridge.ps1`, `inventor-addin/README.md`, `.gitignore` (modify) | Ship the exe; docs. |

---

### Task 1: `vault_tools_cli.py` purchasing mode

**Files:**
- Create: `vault_tools_cli.py`
- Test: `tests/test_vault_tools_cli.py`

**Interfaces:**
- Consumes: `bom_purchasing.generate_from_file(bom_file_path, assembly_number, output_dir="", reference_path="") -> dict` (success keys `output_path`, `matched_parts`, `total_purchased_parts`, `unmatched_parts`, `warnings`; failure `{"error": True, "message"}`), `bom_purchasing.read_bom_file(path) -> DataFrame`.
- Produces: `main(argv: list[str]) -> int`; `run_purchasing(bom: str, assembly: str, out_dir: str, name: str) -> dict`; `write_result(path: str, result: dict) -> None`.

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_vault_tools_cli.py
import json
import shutil
from pathlib import Path

import vault_tools_cli as cli

FIXTURE = Path(__file__).parent / "fixtures" / "CD-001608-inventor-bom.txt"


def test_purchasing_writes_named_workbook_and_result(tmp_path):
    bom = tmp_path / "bom.txt"
    shutil.copy(FIXTURE, bom)
    result_path = tmp_path / "result.json"
    code = cli.main(["purchasing", "--bom", str(bom), "--assembly", "CD-001608",
                     "--out", str(tmp_path), "--name", "CD-001608 Purchasing.xlsx",
                     "--result", str(result_path)])
    result = json.loads(result_path.read_text(encoding="utf-8"))
    assert code == 0, result
    assert result["ok"] is True
    assert Path(result["workbook"]) == tmp_path / "CD-001608 Purchasing.xlsx"
    assert Path(result["workbook"]).is_file()
    assert not (tmp_path / "CD-001608-PurchasingExport.xlsx").exists()
    assert result["lines"] > 0
    assert isinstance(result["unmatched"], list) and isinstance(result["warnings"], list)


def test_purchasing_replaces_existing_workbook(tmp_path):
    bom = tmp_path / "bom.txt"
    shutil.copy(FIXTURE, bom)
    (tmp_path / "A Purchasing.xlsx").write_bytes(b"old")
    cli.main(["purchasing", "--bom", str(bom), "--assembly", "A", "--out", str(tmp_path),
              "--name", "A Purchasing.xlsx", "--result", str(tmp_path / "r.json")])
    assert (tmp_path / "A Purchasing.xlsx").read_bytes()[:2] == b"PK"


def test_purchasing_reads_quoted_description(tmp_path):
    bom = tmp_path / "bom.txt"
    bom.write_text('Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tMaterial\tFile Name\r\n'
                   '1\tCD-000001\tPurchased\tEach\t4\t"1/2"" bolt"\t\tSteel\tCD-000001.ipt\r\n', encoding="utf-8")
    out = cli.run_purchasing(str(bom), "X", str(tmp_path), "X Purchasing.xlsx")
    assert out["ok"] is True, out
    assert out["lines"] == 1


def test_failure_is_reported_in_result_file(tmp_path):
    result_path = tmp_path / "r.json"
    code = cli.main(["purchasing", "--bom", str(tmp_path / "missing.txt"), "--assembly", "X",
                     "--out", str(tmp_path), "--name", "X Purchasing.xlsx", "--result", str(result_path)])
    result = json.loads(result_path.read_text(encoding="utf-8"))
    assert code == 1
    assert result["ok"] is False and result["error"]


def test_unexpected_exception_still_writes_result(tmp_path, monkeypatch):
    monkeypatch.setattr(cli, "run_purchasing", lambda *a: (_ for _ in ()).throw(RuntimeError("boom")))
    result_path = tmp_path / "r.json"
    code = cli.main(["purchasing", "--bom", "x", "--assembly", "X", "--out", str(tmp_path),
                     "--name", "n.xlsx", "--result", str(result_path)])
    assert code == 1
    assert "boom" in json.loads(result_path.read_text(encoding="utf-8"))["error"]
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -3.14 -m pytest tests/test_vault_tools_cli.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'vault_tools_cli'`

- [ ] **Step 3: Implement `vault_tools_cli.py`**

```python
"""SFVaultTools: command-line entry for the SF Vault Bridge ribbon buttons.

Built to a windowed one-file exe (build_vault_tools_exe.bat) and run by the
Inventor add-in. A windowed exe has no stdout, so every result goes to the
file named by --result. Specs:
docs/superpowers/specs/2026-10-12-ribbon-purchasing-sheet-design.md
docs/superpowers/specs/2026-10-12-ribbon-design-review-package-design.md
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any

import bom_purchasing as bp


def write_result(path: str, result: dict[str, Any]) -> None:
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(result, ensure_ascii=False, default=str), encoding="utf-8")


def run_purchasing(bom: str, assembly: str, out_dir: str, name: str) -> dict[str, Any]:
    res = bp.generate_from_file(bom, assembly, out_dir)
    if res.get("error"):
        return {"ok": False, "error": str(res.get("message") or "The purchasing engine failed.")}
    src = Path(res["output_path"])
    dst = Path(out_dir) / name
    if src.resolve() != dst.resolve():
        os.replace(src, dst)
    return {
        "ok": True,
        "workbook": str(dst),
        "lines": int(len(bp.read_bom_file(bom))),
        "matched": int(res.get("matched_parts") or 0),
        "total_purchased": int(res.get("total_purchased_parts") or 0),
        "unmatched": [str(u) for u in (res.get("unmatched_parts") or [])],
        "warnings": [str(w) for w in (res.get("warnings") or [])],
    }


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="SFVaultTools")
    sub = p.add_subparsers(dest="command", required=True)
    pur = sub.add_parser("purchasing")
    pur.add_argument("--bom", required=True)
    pur.add_argument("--assembly", required=True)
    pur.add_argument("--out", required=True)
    pur.add_argument("--name", required=True)
    pur.add_argument("--result", required=True)
    rev = sub.add_parser("review")
    rev.add_argument("--bom", required=True)
    rev.add_argument("--manifest", required=True)
    rev.add_argument("--out", required=True)
    rev.add_argument("--result", required=True)
    return p


def main(argv: list[str]) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "purchasing":
            result = run_purchasing(args.bom, args.assembly, args.out, args.name)
        else:
            import review_package
            result = review_package.run_review(args.bom, args.manifest, args.out)
    except Exception as exc:  # the add-in must always get a result file
        result = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
    write_result(args.result, result)
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    import sys
    sys.exit(main(sys.argv[1:]))
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `py -3.14 -m pytest tests/test_vault_tools_cli.py -v`
Expected: 5 passed. If the quoted-description test shows pandas splitting the field, fix `run_purchasing`'s caller contract by keeping the C# writer's quoting (Task 4) and report it; do not change the test's file text.

- [ ] **Step 5: Commit**

```bash
git add vault_tools_cli.py tests/test_vault_tools_cli.py
git commit -m "SFVaultTools CLI: headless purchasing sheet with result file"
```

---

### Task 2: `review_package.py`

**Files:**
- Create: `review_package.py`
- Test: `tests/test_review_package.py`

**Interfaces:**
- Consumes: `bom_purchasing.read_bom_file`, `pdf_watermark.apply_watermark(pdf_bytes, text, *, font_size=80, color="#888888", opacity=0.3, rotation=45.0) -> bytes` (drops outlines, so bookmarks are added after it).
- Manifest JSON (written by the add-in, Task 4 `ReviewManifest`):
  `{"assembly", "description", "user", "state", "version", "created", "drawings": [{"part_number", "drawing", "pdf"}], "missing": [str], "failed": [{"drawing", "reason"}]}`
- Produces: `run_review(bom: str, manifest: str, out_pdf: str) -> dict` returning `{"ok": True, "pdf", "pages", "drawings", "missing", "failed"}`; constants `WATERMARK = "DRAFT"`, `WATERMARK_COLOR = "#C62828"`, `WATERMARK_OPACITY = 0.25`.

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_review_package.py
import json
from pathlib import Path

from pypdf import PdfReader
from reportlab.pdfgen import canvas

import review_package as rp

HEADER = "Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tMaterial\tFile Name\r\n"


def _bom(path: Path, rows: int) -> Path:
    lines = [HEADER] + [f"{i}\tCD-{i:06d}\tNormal\tEach\t1\tpart {i}\t1\tSteel\tCD-{i:06d}.ipt\r\n" for i in range(1, rows + 1)]
    path.write_text("".join(lines), encoding="utf-8")
    return path


def _drawing(path: Path, label: str, pages: int = 1) -> Path:
    c = canvas.Canvas(str(path), pagesize=(1224, 792))
    for p in range(pages):
        c.drawString(100, 700, f"{label} sheet {p + 1}")
        c.showPage()
    c.save()
    return path


def _manifest(tmp_path: Path, drawings, missing=(), failed=()) -> Path:
    m = {"assembly": "CD-001887", "description": "big blue sub-adapter assembly", "user": "zak@simplifyber.com",
         "state": "Work in Progress", "version": "5", "created": "2026-10-12 17:30",
         "drawings": drawings, "missing": list(missing), "failed": list(failed)}
    p = tmp_path / "manifest.json"
    p.write_text(json.dumps(m), encoding="utf-8")
    return p


def test_order_bookmarks_watermark_and_cover_lists(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "ASSEMBLY", pages=2)
    d2 = _drawing(tmp_path / "b.pdf", "PLATE")
    manifest = _manifest(tmp_path, [
        {"part_number": "CD-001887", "drawing": "CD-001887.idw", "pdf": str(d1)},
        {"part_number": "CD-001891", "drawing": "CD-001891.idw", "pdf": str(d2)},
    ], missing=["CD-001889"], failed=[{"drawing": "CD-001890.idw", "reason": "could not open"}])
    out = tmp_path / "CD-001887 Design Review.pdf"
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 3)), str(manifest), str(out))
    assert res["ok"] is True, res
    reader = PdfReader(str(out))
    titles = [o.title for o in reader.outline]
    assert titles == ["Cover", "BOM", "CD-001887 - CD-001887.idw", "CD-001891 - CD-001891.idw"]
    texts = [page.extract_text() or "" for page in reader.pages]
    assert all("DRAFT" in t for t in texts)
    assert "CD-001889" in texts[0] and "CD-001890.idw" in texts[0] and "could not open" in texts[0]
    assert any("ASSEMBLY sheet 2" in t for t in texts)
    assert texts.index(next(t for t in texts if "ASSEMBLY sheet 1" in t)) < texts.index(next(t for t in texts if "PLATE" in t))
    assert res["pages"] == len(reader.pages)
    assert res["drawings"] == 2 and res["missing"] == 1 and res["failed"] == 1


def test_long_bom_repeats_header_on_every_page(tmp_path):
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 90)), str(_manifest(tmp_path, [])), str(out))
    assert res["ok"] is True
    reader = PdfReader(str(out))
    bom_start = reader.get_destination_page_number(reader.outline[1])
    bom_pages = [p.extract_text() or "" for p in reader.pages[bom_start:]]
    assert len(bom_pages) >= 2
    assert all("Part Number" in t for t in bom_pages)
    assert all("DRAFT" in t for t in bom_pages)


def test_no_drawings_still_builds_cover_and_bom(tmp_path):
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 2)), str(_manifest(tmp_path, [], missing=["CD-1"])), str(out))
    assert res["ok"] is True and res["drawings"] == 0
    assert [o.title for o in PdfReader(str(out)).outline] == ["Cover", "BOM"]


def test_missing_drawing_pdf_is_counted_as_failed(tmp_path):
    out = tmp_path / "r.pdf"
    manifest = _manifest(tmp_path, [{"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(tmp_path / "nope.pdf")}])
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 1)), str(manifest), str(out))
    assert res["ok"] is True and res["drawings"] == 0 and res["failed"] == 1
    assert "CD-1.idw" in (PdfReader(str(out)).pages[0].extract_text() or "")
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -3.14 -m pytest tests/test_review_package.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'review_package'`

- [ ] **Step 3: Implement `review_package.py`**

```python
"""Design review package: cover, BOM table and drawings in one DRAFT PDF.

Run through SFVaultTools.exe review (vault_tools_cli.py). Spec:
docs/superpowers/specs/2026-10-12-ribbon-design-review-package-design.md
"""
from __future__ import annotations

import json
from io import BytesIO
from pathlib import Path
from typing import Any

from pypdf import PdfReader, PdfWriter
from reportlab.lib import colors
from reportlab.lib.pagesizes import landscape, letter
from reportlab.lib.styles import getSampleStyleSheet
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

import bom_purchasing as bp
from pdf_watermark import apply_watermark

WATERMARK = "DRAFT"
WATERMARK_COLOR = "#C62828"
WATERMARK_OPACITY = 0.25
BOM_COLUMNS = ["Item", "Part Number", "Description", "QTY", "BOM Structure", "Material", "REV"]


def _doc(story) -> bytes:
    buf = BytesIO()
    SimpleDocTemplate(buf, pagesize=landscape(letter), leftMargin=36, rightMargin=36,
                      topMargin=36, bottomMargin=36).build(story)
    return buf.getvalue()


def _cover(m: dict[str, Any], drawings: int, failed: list[dict[str, str]]) -> bytes:
    st = getSampleStyleSheet()
    story = [Paragraph(f"Design review: {m.get('assembly', '')}", st["Title"]),
             Paragraph(str(m.get("description") or ""), st["Heading2"]),
             Paragraph(f"Created {m.get('created', '')} by {m.get('user', '')}", st["Normal"]),
             Paragraph(f"Vault state {m.get('state', '')}, version {m.get('version', '')}", st["Normal"]),
             Paragraph(f"Drawings included: {drawings}", st["Normal"]),
             Spacer(1, 12),
             Paragraph("Missing drawings", st["Heading3"])]
    missing = m.get("missing") or []
    story += [Paragraph(str(x), st["Normal"]) for x in missing] or [Paragraph("None", st["Normal"])]
    story += [Spacer(1, 12), Paragraph("Could not export", st["Heading3"])]
    story += [Paragraph(f"{f.get('drawing', '')}: {f.get('reason', '')}", st["Normal"]) for f in failed] \
        or [Paragraph("None", st["Normal"])]
    return _doc(story)


def _bom_table(bom: str) -> bytes:
    df = bp.read_bom_file(bom).fillna("")
    cols = [c for c in BOM_COLUMNS if c in df.columns]
    data = [cols] + [[str(row[c]) for c in cols] for _, row in df.iterrows()]
    table = Table(data, repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1F3864")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONTSIZE", (0, 0), (-1, -1), 8),
        ("GRID", (0, 0), (-1, -1), 0.25, colors.grey),
    ]))
    return _doc([Paragraph("Bill of materials", getSampleStyleSheet()["Heading2"]), table])


def run_review(bom: str, manifest: str, out_pdf: str) -> dict[str, Any]:
    m = json.loads(Path(manifest).read_text(encoding="utf-8"))
    failed = [dict(f) for f in (m.get("failed") or [])]
    usable = []
    for d in m.get("drawings") or []:
        try:
            usable.append((d, PdfReader(str(d["pdf"]))))
        except Exception as exc:
            failed.append({"drawing": d.get("drawing", ""), "reason": f"PDF unreadable: {exc}"})

    writer = PdfWriter()
    marks: list[tuple[str, int]] = []

    def add(title: str, reader: PdfReader) -> None:
        marks.append((title, len(writer.pages)))
        for page in reader.pages:
            writer.add_page(page)

    add("Cover", PdfReader(BytesIO(_cover(m, len(usable), failed))))
    add("BOM", PdfReader(BytesIO(_bom_table(bom))))
    for d, reader in usable:
        add(f"{d.get('part_number', '')} - {d.get('drawing', '')}", reader)

    merged = BytesIO()
    writer.write(merged)
    stamped = apply_watermark(merged.getvalue(), WATERMARK, font_size=120,
                              color=WATERMARK_COLOR, opacity=WATERMARK_OPACITY)
    final = PdfWriter(clone_from=PdfReader(BytesIO(stamped)))
    for title, index in marks:
        final.add_outline_item(title, index)
    Path(out_pdf).parent.mkdir(parents=True, exist_ok=True)
    with open(out_pdf, "wb") as fh:
        final.write(fh)
    return {"ok": True, "pdf": str(out_pdf), "pages": len(final.pages), "drawings": len(usable),
            "missing": len(m.get("missing") or []), "failed": len(failed)}
```

If `PdfWriter(clone_from=...)` is not available in the installed pypdf, use `final = PdfWriter(); final.append(PdfReader(...))` and keep the bookmark indices; say so in the report.

- [ ] **Step 4: Run tests to verify they pass**

Run: `py -3.14 -m pytest tests/test_review_package.py tests/test_vault_tools_cli.py -v`
Expected: all passed

- [ ] **Step 5: Commit**

```bash
git add review_package.py tests/test_review_package.py
git commit -m "Review package: cover, BOM table, drawings, DRAFT watermark, bookmarks"
```

---

### Task 3: Build `SFVaultTools.exe` and ship it with the add-in

**Files:**
- Create: `build_vault_tools_exe.bat`
- Modify: `inventor-addin/Install-VaultBridge.ps1`, `.gitignore`

**Interfaces:**
- Produces: `inventor-addin\tools\SFVaultTools.exe` (not committed), installed next to `SFVaultBridge.Addin.dll` in `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge\`.

- [ ] **Step 1: Write `build_vault_tools_exe.bat`**

```bat
@echo off
rem Builds inventor-addin\tools\SFVaultTools.exe from vault_tools_cli.py (windowed, one file).
cd /d "%~dp0"
py -3.14 -m pip install --quiet pandas openpyxl xlrd pypdf reportlab msal httpx pyinstaller || exit /b 1
py -3.14 -m PyInstaller --onefile --windowed --noconfirm --clean --name SFVaultTools ^
  --distpath "%~dp0inventor-addin\tools" --workpath "%~dp0build\sfvaulttools" --specpath "%~dp0build\sfvaulttools" ^
  --add-data "%~dp0Simplifyber_Logo.png;." --add-data "%~dp0Simplifyber_Logo_White.png;." ^
  --collect-all msal --hidden-import httpx "%~dp0vault_tools_cli.py" || exit /b 1
if not exist "%~dp0inventor-addin\tools\SFVaultTools.exe" exit /b 1
echo Built inventor-addin\tools\SFVaultTools.exe
```

Add to `.gitignore`:

```
inventor-addin/tools/
build/sfvaulttools/
```

- [ ] **Step 2: Build it**

Run: `cmd //c build_vault_tools_exe.bat`
Expected: `Built inventor-addin\tools\SFVaultTools.exe`

- [ ] **Step 3: Smoke-test the exe on the fixture**

```bash
mkdir -p "$TEMP/sfvt" && cp tests/fixtures/CD-001608-inventor-bom.txt "$TEMP/sfvt/bom.txt"
./inventor-addin/tools/SFVaultTools.exe purchasing --bom "$TEMP/sfvt/bom.txt" --assembly CD-001608 --out "$TEMP/sfvt" --name "CD-001608 Purchasing.xlsx" --result "$TEMP/sfvt/result.json"; echo "exit $?"; cat "$TEMP/sfvt/result.json"
```

Expected: `exit 0` and `"ok": true` with a workbook path. If the exe cannot find a module, add the matching `--hidden-import` and rebuild; list every addition in the report.

- [ ] **Step 4: Ship it from the installer**

In `Install-VaultBridge.ps1`, after the `dotnet build` check and before copying, add:

```powershell
$tool = Join-Path $root 'tools\SFVaultTools.exe'
if (-not (Test-Path $tool)) {
    & cmd /c (Join-Path (Split-Path $root -Parent) 'build_vault_tools_exe.bat')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tool)) { throw 'Building SFVaultTools.exe failed.' }
}
```

and after `Copy-Item (Join-Path $out 'SFVaultBridge.*') $dest -Force` add:

```powershell
Copy-Item $tool $dest -Force
```

Do NOT run the installer (Inventor is open; the install happens in Task 7).

- [ ] **Step 5: Commit**

```bash
git add build_vault_tools_exe.bat .gitignore inventor-addin/Install-VaultBridge.ps1
git commit -m "Build SFVaultTools.exe and install it next to the add-in"
```

---

### Task 4: Core: BOM ordering, file writer, paths, review rules, manifest, tool results

**Files:**
- Create: `inventor-addin/SFVaultBridge.Core/Bom.cs`, `inventor-addin/SFVaultBridge.Core/Outputs.cs`
- Test: `inventor-addin/SFVaultBridge.Core.Tests/BomTests.cs`, `inventor-addin/SFVaultBridge.Core.Tests/OutputsTests.cs`

**Interfaces:**
- Produces (namespace `SFVaultBridge.Core`):
  - `record BomNode(string PartNumber, string Description, double Quantity, string UnitQuantity, string BomStructure, string Revision, string Material, string FileName, string FullPath, IReadOnlyList<BomNode> Children)`
  - `record BomLine(string Item, BomNode Node, int Depth)`
  - `static class BomOrdering`: `IReadOnlyList<BomLine> Flatten(IEnumerable<BomNode> roots)`
  - `static class BomFile`: `string[] Headers`, `void Write(string path, IReadOnlyList<BomLine> lines)`, `string Field(string? value)`
  - `static class VaultPaths`: `string ToVaultFolder(string localFile, string workingFolderRoot)`, `string PurchasingName(string assemblyFile)`, `string ReviewName(string assemblyFile)`
  - `static class ReviewRules`: `bool Skip(BomNode node, string? vaultCategory)`, `IReadOnlyList<BomLine> UniqueComponents(IReadOnlyList<BomLine> lines)`, `IReadOnlyList<string> PickDrawings(string componentFileName, IEnumerable<string> drawingFileNames)`, `string Bookmark(string partNumber, string drawingFileName)`
  - `records ReviewDrawing(string PartNumber, string Drawing, string Pdf)`, `ReviewFailure(string Drawing, string Reason)`; `static class ReviewManifest`: `string ToJson(string assembly, string description, string user, string state, string version, DateTime created, IEnumerable<ReviewDrawing> drawings, IEnumerable<string> missing, IEnumerable<ReviewFailure> failed)`
  - `record ToolResult(bool Ok, string? Output, int Count, IReadOnlyList<string> Unmatched, string? Error)`; `static class ToolResults`: `ToolResult Parse(string? json, string outputKey, string countKey)`, `IReadOnlyList<string> PurchasingArgs(string bom, string assembly, string outDir, string name, string resultPath)`, `IReadOnlyList<string> ReviewArgs(string bom, string manifest, string outPdf, string resultPath)`

- [ ] **Step 1: Write the failing tests**

`BomTests.cs`:

```csharp
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class BomTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-bom-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static BomNode N(string pn, string desc, params BomNode[] kids) =>
        new(pn, desc, 1, "Each", "Normal", "1", "Steel", pn + ".ipt", @"C:\Vault Workspace\DESIGNS\" + pn + ".ipt", kids);

    [Fact]
    public void Sorts_siblings_by_description_descending_and_renumbers()
    {
        var roots = new[] { N("A", "alpha"), N("B", "charlie", N("B2", "a"), N("B1", "z")), N("C", "bravo"), N("D", "") };
        var lines = BomOrdering.Flatten(roots);
        Assert.Equal(new[] { "1", "1.1", "1.2", "2", "3", "4" }, lines.Select(l => l.Item));
        Assert.Equal(new[] { "B", "B1", "B2", "C", "A", "D" }, lines.Select(l => l.Node.PartNumber));
        Assert.Equal(new[] { 0, 1, 1, 0, 0, 0 }, lines.Select(l => l.Depth));
    }

    [Fact]
    public void Ties_break_by_part_number()
    {
        var lines = BomOrdering.Flatten(new[] { N("Z", "same"), N("A", "SAME") });
        Assert.Equal(new[] { "A", "Z" }, lines.Select(l => l.Node.PartNumber));
    }

    [Fact]
    public void Writes_headers_and_escapes_fields()
    {
        var path = Path.Combine(_dir, "bom.txt");
        var node = new BomNode("CD-1", "1/2\" bolt\twith tab", 2.5, "Each", "Purchased", "", "Steel", "CD-1.ipt", @"C:\x\CD-1.ipt", Array.Empty<BomNode>());
        BomFile.Write(path, BomOrdering.Flatten(new[] { node }));
        var text = File.ReadAllText(path);
        Assert.StartsWith("Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tMaterial\tFile Name\r\n", text);
        Assert.Contains("1\tCD-1\tPurchased\tEach\t2.5\t\"1/2\"\" bolt with tab\"\t\tSteel\tCD-1.ipt\r\n", text);
    }

    [Fact]
    public void Field_flattens_newlines()
    {
        Assert.Equal("a b", BomFile.Field("a\r\nb"));
        Assert.Equal("", BomFile.Field(null));
    }
}
```

`OutputsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class OutputsTests
{
    private static BomNode N(string pn, string structure = "Normal", string path = "") =>
        new(pn, "d", 1, "Each", structure, "", "", pn + ".ipt", path == "" ? @"C:\Vault Workspace\DESIGNS\" + pn + ".ipt" : path, Array.Empty<BomNode>());

    [Fact]
    public void Maps_local_file_to_vault_folder()
    {
        Assert.Equal("$/DESIGNS/PRODUCTION EQUIPMENT",
            VaultPaths.ToVaultFolder(@"C:\Vault Workspace\DESIGNS\PRODUCTION EQUIPMENT\CD-001887.iam", @"C:\Vault Workspace"));
        Assert.Equal("$", VaultPaths.ToVaultFolder(@"C:\Vault Workspace\a.iam", @"C:\Vault Workspace\"));
    }

    [Fact]
    public void Outside_workspace_asks_to_check_in()
    {
        var ex = Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Users\z\Desktop\a.iam", @"C:\Vault Workspace"));
        Assert.Equal("not_in_vault", ex.Code);
        Assert.Contains("Check the assembly in to Vault first", ex.Message);
        Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Vault WorkspaceX\a.iam", @"C:\Vault Workspace"));
    }

    [Fact]
    public void Output_names()
    {
        Assert.Equal("CD-001887 Purchasing.xlsx", VaultPaths.PurchasingName(@"C:\x\CD-001887.iam"));
        Assert.Equal("CD-001887 Design Review.pdf", VaultPaths.ReviewName(@"C:\x\CD-001887.iam"));
    }

    [Fact]
    public void Skip_rules()
    {
        Assert.True(ReviewRules.Skip(N("A", "Purchased"), null));
        Assert.True(ReviewRules.Skip(N("A"), "Part - Purchased"));
        Assert.True(ReviewRules.Skip(N("A"), "Part - Content Center"));
        Assert.True(ReviewRules.Skip(N("A", path: @"C:\Vault Workspace\Content Center Files\en-US\x.ipt"), null));
        Assert.False(ReviewRules.Skip(N("A"), "Part - Engineering"));
    }

    [Fact]
    public void Unique_components_keep_first_occurrence()
    {
        var a = N("A"); var b = N("B");
        var lines = new[] { new BomLine("1", a, 0), new BomLine("2", b, 0), new BomLine("2.1", a, 1) };
        Assert.Equal(new[] { "1", "2" }, ReviewRules.UniqueComponents(lines).Select(l => l.Item));
    }

    [Fact]
    public void Drawing_choice()
    {
        Assert.Equal(new[] { "CD-1.idw" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-9.idw", "CD-1.dwg", "CD-1.idw" }));
        Assert.Equal(new[] { "CD-1.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.dwg", "X.iam" }));
        Assert.Equal(new[] { "A.idw", "B.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "B.dwg", "A.idw", "A.IDW" }));
        Assert.Empty(ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.pdf" }));
        Assert.Equal("CD-1 - CD-1.idw", ReviewRules.Bookmark("CD-1", "CD-1.idw"));
    }

    [Fact]
    public void Manifest_json_matches_python_contract()
    {
        var json = ReviewManifest.ToJson("CD-001887", "desc", "zak", "Work in Progress", "5", new DateTime(2026, 10, 12, 17, 30, 0),
            new[] { new ReviewDrawing("CD-1", "CD-1.idw", @"C:\t\1.pdf") }, new[] { "CD-2" }, new[] { new ReviewFailure("CD-3.idw", "could not open") });
        var o = (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal("2026-10-12 17:30", o["created"]!.GetValue<string>());
        Assert.Equal("CD-1.idw", o["drawings"]![0]!["drawing"]!.GetValue<string>());
        Assert.Equal(@"C:\t\1.pdf", o["drawings"]![0]!["pdf"]!.GetValue<string>());
        Assert.Equal("CD-1", o["drawings"]![0]!["part_number"]!.GetValue<string>());
        Assert.Equal("CD-2", o["missing"]![0]!.GetValue<string>());
        Assert.Equal("could not open", o["failed"]![0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Tool_result_parsing()
    {
        var ok = ToolResults.Parse("{\"ok\": true, \"workbook\": \"C:\\\\a.xlsx\", \"lines\": 12, \"unmatched\": [\"X\"]}", "workbook", "lines");
        Assert.True(ok.Ok); Assert.Equal(@"C:\a.xlsx", ok.Output); Assert.Equal(12, ok.Count); Assert.Equal(new[] { "X" }, ok.Unmatched);
        var bad = ToolResults.Parse("{\"ok\": false, \"error\": \"No part numbers\"}", "workbook", "lines");
        Assert.False(bad.Ok); Assert.Equal("No part numbers", bad.Error);
        Assert.False(ToolResults.Parse(null, "pdf", "pages").Ok);
        Assert.Contains("bridge.log", ToolResults.Parse("not json", "pdf", "pages").Error);
    }

    [Fact]
    public void Tool_arguments()
    {
        Assert.Equal(new[] { "purchasing", "--bom", "b", "--assembly", "A", "--out", "o", "--name", "n", "--result", "r" },
            ToolResults.PurchasingArgs("b", "A", "o", "n", "r"));
        Assert.Equal(new[] { "review", "--bom", "b", "--manifest", "m", "--out", "p", "--result", "r" },
            ToolResults.ReviewArgs("b", "m", "p", "r"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build FAIL with `The type or namespace name 'BomNode' could not be found`

- [ ] **Step 3: Implement `Bom.cs`**

```csharp
using System.Globalization;
using System.Text;

namespace SFVaultBridge.Core;

public sealed record BomNode(string PartNumber, string Description, double Quantity, string UnitQuantity, string BomStructure,
                             string Revision, string Material, string FileName, string FullPath, IReadOnlyList<BomNode> Children);

public sealed record BomLine(string Item, BomNode Node, int Depth);

/// <summary>Same ordering the manual Inventor export asks for: Description descending, then renumber.</summary>
public static class BomOrdering
{
    public static IReadOnlyList<BomLine> Flatten(IEnumerable<BomNode> roots)
    {
        var lines = new List<BomLine>();
        Walk(roots, "", 0, lines);
        return lines;
    }

    private static void Walk(IEnumerable<BomNode> nodes, string prefix, int depth, List<BomLine> lines)
    {
        var i = 0;
        foreach (var n in nodes.OrderByDescending(n => n.Description ?? "", StringComparer.OrdinalIgnoreCase)
                               .ThenBy(n => n.PartNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            i++;
            var item = prefix.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : prefix + "." + i.ToString(CultureInfo.InvariantCulture);
            lines.Add(new BomLine(item, n, depth));
            Walk(n.Children, item, depth + 1, lines);
        }
    }
}

/// <summary>Tab-delimited BOM in the layout bom_purchasing.read_bom_file reads.</summary>
public static class BomFile
{
    public static readonly string[] Headers = { "Item", "Part Number", "BOM Structure", "Unit QTY", "QTY", "Description", "REV", "Material", "File Name" };

    public static void Write(string path, IReadOnlyList<BomLine> lines)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join("\t", Headers)).Append("\r\n");
        foreach (var l in lines)
        {
            var n = l.Node;
            sb.Append(string.Join("\t", new[]
            {
                Field(l.Item), Field(n.PartNumber), Field(n.BomStructure), Field(n.UnitQuantity),
                Field(n.Quantity.ToString("G", CultureInfo.InvariantCulture)), Field(n.Description),
                Field(n.Revision), Field(n.Material), Field(n.FileName),
            })).Append("\r\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static string Field(string? value)
    {
        var s = (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
```

- [ ] **Step 4: Implement `Outputs.cs`**

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

public static class VaultPaths
{
    public static string ToVaultFolder(string localFile, string workingFolderRoot)
    {
        var root = Path.GetFullPath(workingFolderRoot).TrimEnd('\\', '/') + "\\";
        var full = Path.GetFullPath(localFile);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("not_in_vault", $"{full} is not in the Vault workspace {root.TrimEnd('\\')}. Check the assembly in to Vault first.");
        var dir = Path.GetDirectoryName(full)! + "\\";
        var rel = dir.Substring(root.Length).TrimEnd('\\');
        return rel.Length == 0 ? "$" : "$/" + rel.Replace('\\', '/');
    }

    public static string PurchasingName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Purchasing.xlsx";
    public static string ReviewName(string assemblyFile) => Path.GetFileNameWithoutExtension(assemblyFile) + " Design Review.pdf";
}

public static class ReviewRules
{
    public static bool Skip(BomNode node, string? vaultCategory) =>
        string.Equals(node.BomStructure, "Purchased", StringComparison.OrdinalIgnoreCase)
        || vaultCategory is "Part - Purchased" or "Part - Content Center"
        || node.FullPath.Contains(@"\Content Center Files\", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<BomLine> UniqueComponents(IReadOnlyList<BomLine> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return lines.Where(l => seen.Add(l.Node.FullPath)).ToList();
    }

    public static IReadOnlyList<string> PickDrawings(string componentFileName, IEnumerable<string> drawingFileNames)
    {
        static bool IsDrawing(string d) => d.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(componentFileName);
        var all = drawingFileNames.Where(IsDrawing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var same = all.Where(d => Path.GetFileNameWithoutExtension(d).Equals(stem, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(d => d.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        return same.Count > 0 ? new[] { same[0] } : all.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Bookmark(string partNumber, string drawingFileName) => $"{partNumber} - {drawingFileName}";
}

public sealed record ReviewDrawing(string PartNumber, string Drawing, string Pdf);
public sealed record ReviewFailure(string Drawing, string Reason);

public static class ReviewManifest
{
    public static string ToJson(string assembly, string description, string user, string state, string version, DateTime created,
                                IEnumerable<ReviewDrawing> drawings, IEnumerable<string> missing, IEnumerable<ReviewFailure> failed) =>
        new JsonObject
        {
            ["assembly"] = assembly, ["description"] = description, ["user"] = user, ["state"] = state, ["version"] = version,
            ["created"] = created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["drawings"] = new JsonArray(drawings.Select(d => (JsonNode?)new JsonObject { ["part_number"] = d.PartNumber, ["drawing"] = d.Drawing, ["pdf"] = d.Pdf }).ToArray()),
            ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["failed"] = new JsonArray(failed.Select(f => (JsonNode?)new JsonObject { ["drawing"] = f.Drawing, ["reason"] = f.Reason }).ToArray()),
        }.ToJsonString();
}

public sealed record ToolResult(bool Ok, string? Output, int Count, IReadOnlyList<string> Unmatched, string? Error);

public static class ToolResults
{
    private const string NoResult = "SFVaultTools gave no usable result. See bridge.log.";

    public static ToolResult Parse(string? json, string outputKey, string countKey)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResult);
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) return new ToolResult(false, null, 0, Array.Empty<string>(), NoResult);
        var ok = o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var unmatched = o["unmatched"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : new List<string>();
        var count = o[countKey] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        return new ToolResult(ok, o[outputKey]?.GetValue<string>(), count, unmatched,
                              ok ? null : (o["error"]?.ToString() ?? NoResult));
    }

    public static IReadOnlyList<string> PurchasingArgs(string bom, string assembly, string outDir, string name, string resultPath) =>
        new[] { "purchasing", "--bom", bom, "--assembly", assembly, "--out", outDir, "--name", name, "--result", resultPath };

    public static IReadOnlyList<string> ReviewArgs(string bom, string manifest, string outPdf, string resultPath) =>
        new[] { "review", "--bom", bom, "--manifest", manifest, "--out", outPdf, "--result", resultPath };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: all passed (40 existing + the new ones)

- [ ] **Step 6: Commit**

```bash
git add inventor-addin/SFVaultBridge.Core inventor-addin/SFVaultBridge.Core.Tests
git commit -m "Bridge core: BOM ordering and file, Vault paths, review rules, manifest, tool results"
```

---

### Task 5: Add-in: BOM reader, Vault publish, tool runner, Purchasing Sheet button

**Files:**
- Create: `inventor-addin/SFVaultBridge.Addin/BomReader.cs`, `VaultFiles.cs`, `ToolRunner.cs`, `RibbonActions.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/RibbonStatus.cs` (expose panels), `AddInServer.cs` (create RibbonActions after the status ribbon), `VaultSession.cs` (expose the connection)

**Interfaces:**
- Consumes: Task 4 Core types; existing `RibbonStatus` (`ClientId`, `PictureConverter`, `Dot`, panel creation), `AddInServer` (`_app`, `_log`, `CreateRibbon`), `VaultSession` (`Current`, `Conn`).
- Produces: `RibbonStatus.Panels` (`IReadOnlyList<Inv.RibbonPanel>`); `VaultSession.Connection` (internal static, throws `not_signed_in`); `BomReader.Read(Inv.AssemblyDocument) -> IReadOnlyList<BomNode>`; `VaultFiles` (see code); `ToolRunner.Run(IReadOnlyList<string> args, string resultPath, TimeSpan timeout) -> string?`; `RibbonActions(Inv.Application app, IReadOnlyList<Inv.RibbonPanel> panels, Action<string> log)` with `Create()`/`Destroy()`.

The Inventor and Vault API calls below were written without compiling. Where a member differs, confirm against the referenced assemblies and adapt with the same behavior; list each adaptation in the report. No unit tests for these classes: the gates are a clean Release build, the Core tests, and Task 7's live run.

- [ ] **Step 1: Expose the panels and the connection**

In `RibbonStatus.cs` add:

```csharp
public IReadOnlyList<Inv.RibbonPanel> Panels => _panels;
```

(`_panels` is the existing list the panel loop fills; if its element type differs, expose it as that type.)

In `VaultSession.cs` add:

```csharp
internal static VDF.Vault.Currency.Connections.Connection Connection => Conn;
```

- [ ] **Step 2: Write `BomReader.cs`**

```csharp
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Structured, all-levels BOM of the open assembly as a BomNode tree (unsaved edits included).</summary>
internal static class BomReader
{
    public static IReadOnlyList<BomNode> Read(Inv.AssemblyDocument asm)
    {
        var bom = asm.ComponentDefinition.BOM;
        bom.StructuredViewEnabled = true;
        bom.StructuredViewFirstLevelOnly = false;
        Inv.BOMView? view = null;
        foreach (Inv.BOMView v in bom.BOMViews)
            if (v.ViewType == Inv.BOMViewTypeEnum.kStructuredBOMViewType) { view = v; break; }
        if (view is null) throw new BridgeException("no_bom", "The assembly has no structured BOM view.");
        return Rows(view.BOMRows);
    }

    private static IReadOnlyList<BomNode> Rows(Inv.BOMRowsEnumerator? rows)
    {
        var list = new List<BomNode>();
        if (rows is null) return list;
        foreach (Inv.BOMRow row in rows) list.Add(Node(row));
        return list;
    }

    private static BomNode Node(Inv.BOMRow row)
    {
        var def = row.ComponentDefinitions[1];
        var doc = def.Document as Inv.Document;
        string Prop(string set, string name)
        {
            try { return Convert.ToString(doc?.PropertySets[set][name].Value) ?? ""; } catch { return ""; }
        }
        var fullPath = doc?.FullFileName ?? "";
        var fileName = fullPath.Length > 0 ? System.IO.Path.GetFileName(fullPath) : "";
        var partNumber = Prop("Design Tracking Properties", "Part Number");
        if (string.IsNullOrWhiteSpace(partNumber)) partNumber = System.IO.Path.GetFileNameWithoutExtension(fileName);
        double qty;
        try { qty = Convert.ToDouble(row.ItemQuantity, System.Globalization.CultureInfo.InvariantCulture); } catch { qty = 0; }
        return new BomNode(
            partNumber,
            Prop("Design Tracking Properties", "Description"),
            qty,
            "Each",
            Structure(row.BOMStructure),
            Prop("Inventor Summary Information", "Revision Number"),
            Prop("Design Tracking Properties", "Material"),
            fileName,
            fullPath,
            Rows(row.ChildRows));
    }

    private static string Structure(Inv.BOMStructureEnum s) => s switch
    {
        Inv.BOMStructureEnum.kPurchasedBOMStructure => "Purchased",
        Inv.BOMStructureEnum.kPhantomBOMStructure => "Phantom",
        Inv.BOMStructureEnum.kReferenceBOMStructure => "Reference",
        Inv.BOMStructureEnum.kInseparableBOMStructure => "Inseparable",
        _ => "Normal",
    };
}
```

- [ ] **Step 3: Write `VaultFiles.cs`**

```csharp
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>Vault file work for the ribbon buttons, on Inventor's signed-in session.</summary>
internal static class VaultFiles
{
    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;

    public static string WorkingFolderRoot() => Conn.WorkingFoldersManager.GetWorkingFolder("$").FullPath;

    public static WsFile? FindLatest(string vaultPath)
    {
        var f = Conn.WebServiceManager.DocumentService.FindLatestFilesByPaths(new[] { vaultPath })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? null : f;
    }

    public static string? CategoryName(WsFile f) => f.Cat?.CatName;

    /// <summary>Add the file, or check out and check in a new version. Sets category Documents.</summary>
    public static (WsFile File, bool NewVersion) Publish(string localPath, string vaultFolder, string fileName, string comment)
    {
        var conn = Conn;
        var vaultPath = vaultFolder.TrimEnd('/') + "/" + fileName;
        var existing = FindLatest(vaultPath);
        WsFile result;
        if (existing is null)
        {
            var wsFolder = conn.WebServiceManager.DocumentService.FindFoldersByPaths(new[] { vaultFolder })?.FirstOrDefault();
            if (wsFolder is null || wsFolder.Id <= 0) throw new BridgeException("not_found", $"Vault folder {vaultFolder} does not exist.");
            var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
            using var stream = System.IO.File.OpenRead(localPath);
            var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(localPath),
                                                 Array.Empty<FileAssocParam>(), null, FileClassification.None, false, stream);
            result = conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(added.EntityMasterId);
        }
        else
        {
            if (existing.CheckedOut && existing.CkOutUserId != conn.UserID)
            {
                var who = conn.WebServiceManager.AdminService.GetUserByUserId(existing.CkOutUserId)?.Name ?? "someone else";
                throw new BridgeException("checked_out_by_other", $"{who} has {fileName} checked out.");
            }
            var iteration = new VDF.Vault.Currency.Entities.FileIteration(conn, existing);
            if (!existing.CheckedOut)
            {
                conn.WebServiceManager.DocumentService.CheckoutFile(existing.Id, CheckoutFileOptions.Master,
                    Environment.MachineName, localPath, comment, out _);
                iteration = new VDF.Vault.Currency.Entities.FileIteration(conn,
                    conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(existing.MasterId));
            }
            var checkedIn = conn.FileManager.CheckinFile(iteration, comment, false, null, null, false, null,
                FileClassification.None, false, new VDF.Currency.FilePathAbsolute(localPath));
            result = conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(checkedIn.EntityMasterId);
        }
        SetCategory(result.MasterId, "Documents", comment);
        return (conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(result.MasterId), existing is not null);
    }

    private static void SetCategory(long masterId, string category, string comment)
    {
        var cat = Conn.WebServiceManager.CategoryService.GetCategoriesByEntityClassId("FILE", true)
            .FirstOrDefault(c => c.Name == category) ?? throw new BridgeException("not_found", $"Vault has no file category {category}.");
        Conn.WebServiceManager.DocumentServiceExtensions.UpdateFileCategories(new[] { masterId }, new[] { cat.Id }, comment);
    }

    /// <summary>Latest drawings (.idw/.dwg) that reference the given model file.</summary>
    public static IReadOnlyList<WsFile> WhereUsedDrawings(WsFile model)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var assocs = ds.GetFileAssociationLitesByIds(new[] { model.Id }, FileAssocAlg.LatestConsolidated,
            FileAssociationTypeEnum.Dependency, false, FileAssociationTypeEnum.None, false, false, false, false) ?? Array.Empty<FileAssocLite>();
        var parentIds = assocs.Where(a => a.CldFileId == model.Id).Select(a => a.ParFileId).Distinct().ToArray();
        if (parentIds.Length == 0) return Array.Empty<WsFile>();
        return ds.GetFilesByIds(parentIds)
            .Where(f => f.Name.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.MasterId).Select(g => ds.GetLatestFileByMasterId(g.Key)).ToList();
    }

    /// <summary>Download the latest version of each drawing and its references; never over files checked out to the user.</summary>
    public static IReadOnlyList<string> AcquireLatest(IEnumerable<WsFile> files)
    {
        var conn = Conn;
        var settings = new VDF.Vault.Settings.AcquireFilesSettings(conn, false);
        settings.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = true;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.RecurseChildren = true;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.VersionGatheringOption = VDF.Vault.Currency.VersionGatheringOption.Latest;
        settings.OptionsResolution.OverwriteOption = VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.NoOverwrite;
        foreach (var f in files)
            settings.AddFileToAcquire(new VDF.Vault.Currency.Entities.FileIteration(conn, f), VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
        var results = conn.FileManager.AcquireFiles(settings);
        return results.FileResults.Select(r => r.LocalPath.FullPath).ToList();
    }

    public static string LocalPathOf(WsFile f) =>
        Conn.WorkingFoldersManager.GetPathOfFileInWorkingFolder(new VDF.Vault.Currency.Entities.FileIteration(Conn, f)).FullPath;
}
```

`NoOverwrite` must leave files checked out to the user untouched; if the real option set differs, pick the one with that behavior and name it in the report.

- [ ] **Step 4: Write `ToolRunner.cs`**

```csharp
using System.Diagnostics;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>Runs SFVaultTools.exe (installed next to this add-in) and returns its result file text.</summary>
internal static class ToolRunner
{
    public static string ToolPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ToolRunner).Assembly.Location)!, "SFVaultTools.exe");

    public static string? Run(IReadOnlyList<string> args, string resultPath, TimeSpan timeout)
    {
        if (!System.IO.File.Exists(ToolPath))
            throw new BridgeException("tool_missing", $"SFVaultTools.exe is not installed next to the add-in ({ToolPath}). Rerun Install-VaultBridge.ps1.");
        var psi = new ProcessStartInfo(ToolPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new BridgeException("tool_failed", "SFVaultTools.exe did not start.");
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new BridgeException("tool_timeout", $"SFVaultTools.exe did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        return System.IO.File.Exists(resultPath) ? System.IO.File.ReadAllText(resultPath) : null;
    }
}
```

- [ ] **Step 5: Write `RibbonActions.cs` (Purchasing Sheet now; Design Review added in Task 6)**

```csharp
using System.Windows.Forms;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>The Purchasing Sheet and Design Review buttons on the SF Vault Bridge panel.</summary>
internal sealed class RibbonActions
{
    private const string ClientId = "{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
    private readonly Inv.Application _app;
    private readonly IReadOnlyList<Inv.RibbonPanel> _panels;
    private readonly Action<string> _log;
    private readonly List<(Inv.ButtonDefinition Def, Inv.ButtonDefinitionSink_OnExecuteEventHandler Handler)> _buttons = new();
    private bool _running;

    public RibbonActions(Inv.Application app, IReadOnlyList<Inv.RibbonPanel> panels, Action<string> log)
    {
        _app = app; _panels = panels; _log = log;
    }

    public void Create()
    {
        Add("Purchasing Sheet", "SFVaultBridge.Purchasing", "Build the purchasing sheet for this assembly and check it into Vault.", Purchasing);
    }

    private void Add(string label, string internalName, string tip, Action<Inv.AssemblyDocument> work)
    {
        try
        {
            var defs = _app.CommandManager.ControlDefinitions;
            Inv.ButtonDefinition? def = null;
            try { def = defs[internalName] as Inv.ButtonDefinition; } catch { }
            def ??= defs.AddButtonDefinition(label, internalName, Inv.CommandTypesEnum.kNonShapeEditCmdType, ClientId, tip, tip,
                                             Type.Missing, Type.Missing, Inv.ButtonDisplayEnum.kAlwaysDisplayText);
            Inv.ButtonDefinitionSink_OnExecuteEventHandler handler = _ => Click(label, work);
            def.OnExecute += handler;
            _buttons.Add((def, handler));
            foreach (var panel in _panels)
            {
                try { panel.CommandControls.AddButton(def, true); }
                catch (Exception ex) { _log($"{label} button on a panel skipped: {ex.Message}"); }
            }
        }
        catch (Exception ex) { _log($"{label} button not created: {ex}"); }
    }

    public void Destroy()
    {
        foreach (var (def, handler) in _buttons)
        {
            try { def.OnExecute -= handler; } catch { }
            try { def.Delete(); } catch { }
        }
        _buttons.Clear();
    }

    private void Click(string label, Action<Inv.AssemblyDocument> work)
    {
        if (_running) { MessageBox.Show("Already running.", label); return; }
        if (_app.ActiveDocument is not Inv.AssemblyDocument asm) { MessageBox.Show("Open an assembly first.", label); return; }
        _running = true;
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            work(asm);
        }
        catch (BridgeException ex) { _log($"{label}: {ex.Code} {ex.Message}"); MessageBox.Show(ex.Message, label); }
        catch (Exception ex) { _log($"{label} failed: {ex}"); MessageBox.Show($"{label} failed: {ex.Message}\nDetails are in bridge.log.", label); }
        finally { Cursor.Current = cursor; _running = false; }
    }

    private static (string Folder, string VaultFolder, string Stem) Locate(Inv.AssemblyDocument asm)
    {
        var file = asm.FullFileName;
        if (string.IsNullOrWhiteSpace(file)) throw new BridgeException("not_in_vault", "Save the assembly and check it in to Vault first.");
        var vaultFolder = VaultPaths.ToVaultFolder(file, VaultFiles.WorkingFolderRoot());
        return (System.IO.Path.GetDirectoryName(file)!, vaultFolder, System.IO.Path.GetFileNameWithoutExtension(file));
    }

    private void Purchasing(Inv.AssemblyDocument asm)
    {
        var (folder, vaultFolder, stem) = Locate(asm);
        var lines = BomOrdering.Flatten(BomReader.Read(asm));
        if (lines.Count == 0) throw new BridgeException("no_bom", "The assembly has no BOM rows.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var bom = System.IO.Path.Combine(temp, "bom.txt");
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            BomFile.Write(bom, lines);
            var name = VaultPaths.PurchasingName(asm.FullFileName);
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.PurchasingArgs(bom, stem, folder, name, resultPath), resultPath, ToolTimeout), "workbook", "lines");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The purchasing tool failed.");
            Publish(result.Output!, vaultFolder, name, $"Purchasing sheet for {stem}",
                    $"{result.Count} BOM lines." + (result.Unmatched.Count > 0 ? $"\nNot matched: {string.Join(", ", result.Unmatched)}" : ""));
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    private void Publish(string localPath, string vaultFolder, string name, string comment, string summary)
    {
        try
        {
            var (file, newVersion) = VaultFiles.Publish(localPath, vaultFolder, name, comment);
            _log($"published {vaultFolder}/{name} version {file.VerNum}");
            var answer = MessageBox.Show($"{(newVersion ? "Updated" : "Added")} {vaultFolder}/{name} (version {file.VerNum}).\n{summary}\n\nOpen it now?",
                                         "SF Vault Bridge", MessageBoxButtons.YesNo);
            if (answer == DialogResult.Yes) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(localPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            throw new BridgeException("vault_failed", $"Saved locally at {localPath} but not checked in to Vault: {ex.Message}");
        }
    }
}
```

- [ ] **Step 6: Create the actions after the status ribbon**

In `AddInServer.cs`, add a field `private RibbonActions? _actions;`. At the end of `CreateRibbon`'s try block (after `_ribbon.Create();`):

```csharp
_actions = new RibbonActions(_app!, _ribbon.Panels, _log!.Write);
_actions.Create();
```

In `Deactivate`, before the ribbon is destroyed: `try { _actions?.Destroy(); } catch (Exception ex) { _log?.Write("actions destroy failed: " + ex); } _actions = null;`

- [ ] **Step 7: Build and test**

Run: `dotnet build inventor-addin/SFVaultBridge.sln -c Release` then `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: 0 errors, all tests pass. Do NOT run the installer or touch Inventor/Vault.

- [ ] **Step 8: Commit**

```bash
git add inventor-addin/SFVaultBridge.Addin
git commit -m "Ribbon: Purchasing Sheet button reads the BOM, runs SFVaultTools, checks the workbook into Vault"
```

---

### Task 6: Add-in: drawing export and the Design Review button

**Files:**
- Create: `inventor-addin/SFVaultBridge.Addin/DrawingExporter.cs`
- Modify: `inventor-addin/SFVaultBridge.Addin/RibbonActions.cs` (add the button and `Review`)

**Interfaces:**
- Consumes: Task 4 `ReviewRules`, `ReviewManifest`, `ReviewDrawing`, `ReviewFailure`, `ToolResults.ReviewArgs`; Task 5 `VaultFiles` (`FindLatest`, `CategoryName`, `WhereUsedDrawings`, `AcquireLatest`, `LocalPathOf`, `Publish`), `ToolRunner`, `RibbonActions.Locate/Publish/Add`, `VaultSession.Connection`.
- Produces: `DrawingExporter.Export(Inv.Application app, string drawingPath, string outPdf)`.

- [ ] **Step 1: Write `DrawingExporter.cs`**

```csharp
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Export every sheet of a drawing to PDF with Inventor's PDF translator, invisibly.</summary>
internal static class DrawingExporter
{
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";

    public static void Export(Inv.Application app, string drawingPath, string outPdf)
    {
        var translator = app.ApplicationAddIns.ItemById[PdfTranslatorId] as Inv.TranslatorAddIn
                         ?? throw new BridgeException("no_pdf_translator", "Inventor's PDF translator add-in is not available.");
        if (!translator.Activated) translator.Activate();
        var silent = app.SilentOperation;
        app.SilentOperation = true;
        Inv.Document? doc = null;
        try
        {
            doc = app.Documents.Open(drawingPath, false);
            var ctx = app.TransientObjects.CreateTranslationContext();
            ctx.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
            var options = app.TransientObjects.CreateNameValueMap();
            if (translator.HasSaveCopyAsOptions[doc, ctx, options])
            {
                options.Value["All_Color_AS_Black"] = 0;
                options.Value["Remove_Line_Weights"] = 0;
                options.Value["Vector_Resolution"] = 400;
                options.Value["Sheet_Range"] = Inv.PrintRangeEnum.kPrintAllSheets;
            }
            var medium = app.TransientObjects.CreateDataMedium();
            medium.FileName = outPdf;
            translator.SaveCopyAs(doc, ctx, options, medium);
            if (!System.IO.File.Exists(outPdf)) throw new BridgeException("export_failed", "Inventor did not write the PDF.");
        }
        finally
        {
            try { doc?.Close(true); } catch { }
            try { app.SilentOperation = silent; } catch { }
        }
    }
}
```

- [ ] **Step 2: Add the Design Review button and flow to `RibbonActions.cs`**

In `Create()` add after the purchasing button:

```csharp
Add("Design Review", "SFVaultBridge.Review", "Build the DRAFT design review PDF for this assembly and check it into Vault.", Review);
```

Add the method:

```csharp
private void Review(Inv.AssemblyDocument asm)
{
    var (_, vaultFolder, stem) = Locate(asm);
    var lines = BomOrdering.Flatten(BomReader.Read(asm));
    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(temp);
    try
    {
        var drawings = new List<ReviewDrawing>();
        var missing = new List<string>();
        var failed = new List<ReviewFailure>();
        var root = VaultFiles.WorkingFolderRoot();

        void Collect(string partNumber, string localModel)
        {
            var vaultModel = VaultPaths.ToVaultFolder(localModel, root) + "/" + System.IO.Path.GetFileName(localModel);
            var model = VaultFiles.FindLatest(vaultModel);
            if (model is null) { missing.Add($"{partNumber} (not in Vault)"); return; }
            var candidates = VaultFiles.WhereUsedDrawings(model);
            var picked = ReviewRules.PickDrawings(model.Name, candidates.Select(c => c.Name));
            if (picked.Count == 0) { missing.Add(partNumber); return; }
            foreach (var name in picked)
            {
                var file = candidates.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                try
                {
                    VaultFiles.AcquireLatest(new[] { file });
                    var local = VaultFiles.LocalPathOf(file);
                    var pdf = System.IO.Path.Combine(temp, $"{drawings.Count + failed.Count:D3}.pdf");
                    DrawingExporter.Export(_app, local, pdf);
                    drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf));
                }
                catch (Exception ex)
                {
                    _log($"review export {file.Name} failed: {ex}");
                    failed.Add(new ReviewFailure(file.Name, ex.Message));
                }
            }
        }

        Collect(stem, asm.FullFileName);
        foreach (var line in ReviewRules.UniqueComponents(lines))
        {
            var n = line.Node;
            if (string.IsNullOrWhiteSpace(n.FullPath)) continue;
            string? category = null;
            try
            {
                var f = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(n.FullPath, root) + "/" + n.FileName);
                category = f is null ? null : VaultFiles.CategoryName(f);
            }
            catch (BridgeException) { }
            if (ReviewRules.Skip(n, category)) continue;
            Collect(n.PartNumber, n.FullPath);
        }

        var bom = System.IO.Path.Combine(temp, "bom.txt");
        BomFile.Write(bom, lines);
        var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
        var conn = VaultSession.Connection;
        var manifest = System.IO.Path.Combine(temp, "manifest.json");
        System.IO.File.WriteAllText(manifest, ReviewManifest.ToJson(stem,
            Convert.ToString(asm.PropertySets["Design Tracking Properties"]["Description"].Value) ?? "",
            conn.UserName, asmFile?.FileLfCyc?.LfCycStateName ?? "", asmFile?.VerNum.ToString() ?? "",
            DateTime.Now, drawings, missing, failed));

        var name = VaultPaths.ReviewName(asm.FullFileName);
        var outPdf = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asm.FullFileName)!, name);
        var resultPath = System.IO.Path.Combine(temp, "result.json");
        var result = ToolResults.Parse(ToolRunner.Run(ToolResults.ReviewArgs(bom, manifest, outPdf, resultPath), resultPath, ToolTimeout), "pdf", "pages");
        if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The review tool failed.");
        Publish(result.Output!, vaultFolder, name, $"Design review for {stem}",
                $"{result.Count} pages, {drawings.Count} drawings, {missing.Count} missing, {failed.Count} could not export.");
    }
    finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
}
```

- [ ] **Step 3: Build and test**

Run: `dotnet build inventor-addin/SFVaultBridge.sln -c Release` then `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: 0 errors, all tests pass. Do NOT run the installer or touch Inventor/Vault.

- [ ] **Step 4: Update `inventor-addin/README.md`**

Add a "Buttons" section: Purchasing Sheet (what it writes, where, new version on rerun), Design Review (DRAFT watermark, cover lists, latest drawings, where it goes), both need Vault sign-in and an assembly that is in Vault, and that a run blocks Inventor until it finishes (a large review can take minutes).

- [ ] **Step 5: Commit**

```bash
git add inventor-addin
git commit -m "Ribbon: Design Review button exports drawings and checks the DRAFT PDF into Vault"
```

---

### Task 7: Install and live test (with Zak)

**Files:** none (live run).

- [ ] **Step 1:** Zak closes Inventor. Run `pwsh -NoProfile -File inventor-addin/Install-VaultBridge.ps1`. Expected: installed, and `SFVaultTools.exe` present in `%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge\`.
- [ ] **Step 2:** Zak starts Inventor, unblocks the add-in if asked, signs in to Vault, opens CD-001887. Panel shows the status dot plus **Purchasing Sheet** and **Design Review**.
- [ ] **Step 3:** Purchasing Sheet: message names `$/DESIGNS/PRODUCTION EQUIPMENT/CD-001887 Purchasing.xlsx` version 1; check over REST (category Documents) and open the workbook. Click again: version 2.
- [ ] **Step 4:** Design Review: PDF in the same Vault folder; open it: DRAFT on every page, cover lists missing drawings, assembly drawing first, purchased parts (CD-001888, CD-001894) absent, bookmarks present.
- [ ] **Step 5:** With a part open, both buttons say "Open an assembly first." Report results; no success claim without the REST check.
