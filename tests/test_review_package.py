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


def test_cover_escapes_markup_characters(tmp_path):
    out = tmp_path / "r.pdf"
    m = {"assembly": "CD-1", "description": 'Plate <1/4" & gusset', "user": "zak@simplifyber.com",
         "state": "Work in Progress", "version": "1", "created": "2026-10-12 17:30",
         "drawings": [], "missing": ["CD-<9>"], "failed": [{"drawing": "CD-2.idw", "reason": "bad <x> & y"}]}
    manifest = tmp_path / "manifest.json"
    manifest.write_text(json.dumps(m), encoding="utf-8")
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 1)), str(manifest), str(out))
    assert res["ok"] is True, res
    cover = PdfReader(str(out)).pages[0].extract_text() or ""
    assert 'Plate <1/4"' in cover and "& gusset" in cover
    assert "CD-<9>" in cover
    assert "bad <x> & y" in cover
