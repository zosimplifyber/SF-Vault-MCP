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


def test_purchased_section_bookmarks_and_no_vendor_list(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "ASSEMBLY")
    p1 = _drawing(tmp_path / "p1.pdf", "BOLTSHEET")
    p2 = _drawing(tmp_path / "p2.pdf", "WIDGETSHEET")
    p3 = _drawing(tmp_path / "p3.pdf", "BARESHEET")
    manifest = _manifest(tmp_path, [{"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1)}])
    m = json.loads(manifest.read_text(encoding="utf-8"))
    m["purchased"] = [
        {"part_number": "CD-7", "vendor": "MCMaster-Carr", "vendor_number": "91251A", "pdf": str(p1)},
        {"part_number": "CD-8", "vendor": "Acme <Co>", "vendor_number": "W-1", "pdf": str(p2)},
        {"part_number": "CD-9", "vendor": "", "vendor_number": "", "pdf": str(p3)},
        {"part_number": "CD-10", "vendor": "", "vendor_number": "", "pdf": str(tmp_path / "nope.pdf")},
    ]
    m["no_vendor_drawing"] = ["CD-11", "CD-<12>"]
    manifest.write_text(json.dumps(m), encoding="utf-8")
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 1)), str(manifest), str(out))
    assert res["ok"] is True, res
    reader = PdfReader(str(out))
    titles = [o.title for o in reader.outline]
    assert titles == ["Cover", "BOM", "CD-1 - CD-1.idw",
                      "Purchased: CD-7 (McMaster 91251A)", "Purchased: CD-8 (Acme <Co> W-1)", "Purchased: CD-9"]
    texts = [p.extract_text() or "" for p in reader.pages]
    assert all("DRAFT" in t for t in texts)
    cover = texts[0]
    assert "No vendor drawing" in cover and "CD-11" in cover and "CD-<12>" in cover
    assert "CD-10" in cover and "PDF unreadable" in cover
    assert res["failed"] == 1 and res["purchased"] == 3 and res["no_vendor_drawing"] == 2
    order = [next(i for i, t in enumerate(texts) if k in t) for k in ("ASSEMBLY", "BOLTSHEET", "WIDGETSHEET", "BARESHEET")]
    assert order == sorted(order)


def test_cover_shows_none_for_empty_no_vendor_list(tmp_path):
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 1)), str(_manifest(tmp_path, [])), str(out))
    assert res["ok"] is True
    cover = PdfReader(str(out)).pages[0].extract_text() or ""
    assert "No vendor drawing" in cover


# ---- assembly mode ----

def _assembly_manifest(tmp_path: Path, drawings, revision="3", **extra) -> Path:
    m = {"mode": "assembly", "revision": revision, "assembly": "CD-001887",
         "description": "big blue sub-adapter assembly", "user": "zak@simplifyber.com",
         "state": "Work in Progress", "version": "5", "created": "2026-10-13 07:00",
         "drawings": drawings, "missing": ["CD-001889"], "failed": []}
    m.update(extra)
    p = tmp_path / "manifest.json"
    p.write_text(json.dumps(m), encoding="utf-8")
    return p


def _build_assembly(tmp_path: Path, drawings, revision="3", **extra):
    out = tmp_path / "asm.pdf"
    manifest = _assembly_manifest(tmp_path, drawings, revision, **extra)
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 2)), str(manifest), str(out))
    reader = PdfReader(str(out))
    return res, reader, [p.extract_text() or "" for p in reader.pages]


def test_assembly_stamps_only_unreleased_pages_and_footer_everywhere(tmp_path):
    import datetime
    d1 = _drawing(tmp_path / "a.pdf", "RELDRAWING", pages=1)
    d2 = _drawing(tmp_path / "b.pdf", "WIPDRAWING", pages=2)
    d3 = _drawing(tmp_path / "c.pdf", "LATERREL", pages=1)
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-001887", "drawing": "CD-001887.idw", "pdf": str(d1), "state": "Released"},
        {"part_number": "CD-001891", "drawing": "CD-001891.idw", "pdf": str(d2), "state": "Work in Progress"},
        {"part_number": "CD-001892", "drawing": "CD-001892.idw", "pdf": str(d3), "state": "Released"},
    ])
    assert res["ok"] is True, res
    assert res["mode"] == "assembly" and res["unreleased"] == 1 and res["drawings"] == 3
    assert [o.title for o in reader.outline] == ["Cover", "BOM", "CD-001887 - CD-001887.idw",
                                                  "CD-001891 - CD-001891.idw", "CD-001892 - CD-001892.idw"]
    idx = lambda key: [i for i, t in enumerate(texts) if key in t]
    assert idx("NOT RELEASED") == idx("WIPDRAWING") and len(idx("NOT RELEASED")) == 2
    assert idx("RELDRAWING") and not set(idx("RELDRAWING")) & set(idx("NOT RELEASED"))
    assert not set(idx("LATERREL")) & set(idx("NOT RELEASED"))
    assert all("DRAFT" not in t for t in texts)
    footer = "FOR ASSEMBLY | CD-001887 Rev 3 |"
    assert all(footer in t for t in texts), texts
    assert all(datetime.date.today().isoformat() in t for t in texts)
    cover = texts[0]
    assert "Assembly package: CD-001887" in cover and "Revision 3" in cover
    assert "CD-001891 - CD-001891.idw (Work in Progress)" in cover
    assert "CD-001889" in cover and "No vendor drawing" not in cover


def test_assembly_empty_revision_renders_dash(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "ONLY")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-001887", "drawing": "CD-001887.idw", "pdf": str(d1), "state": "Released"}], revision="")
    assert all("FOR ASSEMBLY | CD-001887 Rev - |" in t for t in texts)
    assert "Revision -" in texts[0]
    assert res["unreleased"] == 0


def test_assembly_state_case_and_whitespace_count_as_released(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "ONE")
    d2 = _drawing(tmp_path / "b.pdf", "TWO")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1), "state": "released "},
        {"part_number": "CD-2", "drawing": "CD-2.idw", "pdf": str(d2), "state": "RELEASED"}])
    assert res["unreleased"] == 0
    assert all("NOT RELEASED" not in t for t in texts)


def test_assembly_missing_state_is_unreleased_and_unknown_on_cover(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "NOSTATE")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1)}])
    assert res["unreleased"] == 1
    assert any("NOT RELEASED" in t and "NOSTATE" in t for t in texts)
    assert "(unknown)" in texts[0]


def test_assembly_only_assembly_drawing_gives_cover_bom_drawing(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "TOP")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1), "state": "Released"}])
    assert [o.title for o in reader.outline] == ["Cover", "BOM", "CD-1 - CD-1.idw"]
    assert res["pages"] == 3


def test_assembly_ignores_purchased(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "TOP")
    p1 = _drawing(tmp_path / "p1.pdf", "BOLTSHEET")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1), "state": "Released"}],
        purchased=[{"part_number": "CD-7", "vendor": "x", "vendor_number": "1", "pdf": str(p1)}],
        no_vendor_drawing=["CD-11"])
    assert all("BOLTSHEET" not in t for t in texts)
    assert [o.title for o in reader.outline] == ["Cover", "BOM", "CD-1 - CD-1.idw"]
    assert res["purchased"] == 0 and "CD-11" not in texts[0]


def test_manifest_without_mode_is_review(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "TOP")
    out = tmp_path / "r.pdf"
    manifest = _manifest(tmp_path, [{"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1)}])
    res = rp.run_review(str(_bom(tmp_path / "bom.txt", 1)), str(manifest), str(out))
    texts = [p.extract_text() or "" for p in PdfReader(str(out)).pages]
    assert res["mode"] == "review"
    assert all("DRAFT" in t for t in texts)
    assert all("FOR ASSEMBLY" not in t and "NOT RELEASED" not in t for t in texts)


def test_assembly_note_shown_after_state_on_cover_and_escaped(tmp_path):
    d1 = _drawing(tmp_path / "a.pdf", "NOTED")
    res, reader, texts = _build_assembly(tmp_path, [
        {"part_number": "CD-1", "drawing": "CD-1.idw", "pdf": str(d1), "state": "",
         "note": "printed from the copy open <b>in</b> Inventor"}])
    assert "(unknown) - printed from the copy open <b>in</b> Inventor" in " ".join(texts[0].split())


def test_watermark_honours_mediabox_origin(tmp_path):
    from io import BytesIO
    from pypdf.generic import RectangleObject
    from pypdf import PdfWriter
    import pdf_watermark as pw
    src = _drawing(tmp_path / "o.pdf", "ORIGIN")
    reader = PdfReader(str(src))
    w = PdfWriter()
    page = reader.pages[0]
    page.mediabox = RectangleObject((100, 100, 1324, 892))
    w.add_page(page)
    buf = BytesIO()
    w.write(buf)
    out = pw.apply_watermark_to_pages(buf.getvalue(), "NOT RELEASED", [0])
    pg = PdfReader(BytesIO(out)).pages[0]
    seen = []
    pg.extract_text(visitor_text=lambda t, cm, tm, fd, fs: seen.append((t, cm, tm)) if "NOT RELEASED" in t else None)
    assert seen
    _, cm, tm = seen[0]
    # the watermark is rotated about the page centre: (100 + 612, 100 + 396), not (612, 396)
    assert abs(cm[4] - 712) < 1 and abs(cm[5] - 496) < 1, (cm[4], cm[5])


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


def test_bom_yellow_only_for_normal_rows_not_released(tmp_path):
    rows = [("1", "Normal", "Work in Progress"), ("2", "Purchased", "Work in Progress"),
            ("3", "Normal", "Released"), ("4", "Normal", ""), ("5", "Phantom", ""), ("6", "Reference", "Work in Progress")]
    lines = [STATE_HEADER] + [f"{i}\tCD-00002{i}\t{bs}\tEach\t1\tpart {i}\tA\t{st}\tSteel\t\tCD-00002{i}.ipt\t\t\r\n" for i, bs, st in rows]
    p = tmp_path / "bom.txt"
    p.write_text("".join(lines), encoding="utf-8")
    _, yellow = rp.bom_table_data(str(p))
    assert yellow == [1, 4]


def test_bom_table_fits_the_page_width(tmp_path):
    from io import BytesIO
    from pypdf import PdfReader
    long = "93501A035_Steel Heavy-Vibration-Resistant Spring Lock Washers For Socket Head Screws"
    lines = [STATE_HEADER, f"1\t{long}\tPurchased\tEach\t12\t{long} {long}\tANY\tWork in Progress\tSteel\t\tx.ipt\t\t\r\n"]
    p = tmp_path / "bom.txt"
    p.write_text("".join(lines), encoding="utf-8")
    pdf = rp._bom_table(str(p))
    page = PdfReader(BytesIO(pdf)).pages[0]
    xs = []
    page.extract_text(visitor_text=lambda t, cm, tm, fd, fs: xs.append(cm[4] + tm[4]) if t.strip() else None)
    width = float(page.mediabox.width)
    assert xs and min(xs) >= 30 and max(xs) <= width - 30, (min(xs), max(xs), width)
    assert "Work in Progress" in " ".join(page.extract_text().split())


def test_bom_without_state_column_has_no_yellow_rows(tmp_path):
    data, yellow = rp.bom_table_data(str(_bom(tmp_path / "bom.txt", 3)))
    assert "State" not in data[0] and yellow == []


def test_review_pdf_bom_page_shows_state(tmp_path):
    out = tmp_path / "r.pdf"
    res = rp.run_review(str(_state_bom(tmp_path / "bom.txt")), str(_manifest(tmp_path, [])), str(out))
    assert res["ok"] is True
    assert "State" in (PdfReader(str(out)).pages[1].extract_text() or "")
