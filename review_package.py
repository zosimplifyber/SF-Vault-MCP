"""Design review package: cover, BOM table and drawings in one DRAFT PDF.

Run through SFVaultTools.exe review (vault_tools_cli.py). Spec:
docs/superpowers/specs/2026-10-12-ribbon-design-review-package-design.md
"""
from __future__ import annotations

import json
from io import BytesIO
from datetime import date
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape

from pypdf import PdfReader, PdfWriter
from reportlab.lib import colors
from reportlab.lib.pagesizes import landscape, letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

import bom_purchasing as bp
from pdf_watermark import apply_watermark, apply_watermark_to_pages

WATERMARK = "DRAFT"
WATERMARK_COLOR = "#C62828"
WATERMARK_OPACITY = 0.25
UNRELEASED_TEXT = "NOT RELEASED"
FOOTER_COLOR = "#666666"
FOOTER_SIZE = 7
FOOTER_BOTTOM = 18
BOM_COLUMNS = ["Item", "Part Number", "Description", "QTY", "BOM Structure", "Material", "REV", "State"]


def _doc(story) -> bytes:
    buf = BytesIO()
    SimpleDocTemplate(buf, pagesize=landscape(letter), leftMargin=36, rightMargin=36,
                      topMargin=36, bottomMargin=36).build(story)
    return buf.getvalue()


def _para(text: Any, style) -> Paragraph:
    # Paragraph parses markup, so every interpolated value must be escaped.
    return Paragraph(escape(str(text)), style)


def _is_released(d: dict[str, Any]) -> bool:
    return str(d.get("state") or "").strip().lower() == "released"


def _assembly_cover(m: dict[str, Any], usable: list, failed: list[dict[str, str]]) -> bytes:
    st = getSampleStyleSheet()
    story = [_para(f"Assembly package: {m.get('assembly', '')}", st["Title"]),
             _para(m.get("description") or "", st["Heading2"]),
             _para(f"Created {m.get('created', '')} by {m.get('user', '')}", st["Normal"]),
             _para(f"Vault state {m.get('state', '')}, version {m.get('version', '')}", st["Normal"]),
             _para(f"Revision {m.get('revision') or '-'}", st["Normal"]),
             Spacer(1, 12), _para("Included drawings", st["Heading3"])]
    story += [_para(f"{d.get('part_number', '')} - {d.get('drawing', '')} ({str(d.get('state') or '').strip() or 'unknown'})"
                    + (f" - {d['note']}" if d.get("note") else ""), st["Normal"]) for d, _ in usable] or [_para("None", st["Normal"])]
    story += [Spacer(1, 12), _para("Missing drawings", st["Heading3"])]
    story += [_para(x, st["Normal"]) for x in (m.get("missing") or [])] or [_para("None", st["Normal"])]
    story += [Spacer(1, 12), _para("Could not export", st["Heading3"])]
    story += [_para(f"{f.get('drawing', '')}: {f.get('reason', '')}", st["Normal"]) for f in failed] or [_para("None", st["Normal"])]
    return _doc(story)


def _apply_footer(pdf_bytes: bytes, text: str) -> bytes:
    reader = PdfReader(BytesIO(pdf_bytes))
    writer = PdfWriter()
    cache: dict[tuple[float, float], Any] = {}
    for page in reader.pages:
        box = page.mediabox
        left, bottom = float(box.left), float(box.bottom)
        size = (float(box.width), float(box.height))
        key = (*size, left, bottom)
        if key not in cache:
            buf = BytesIO()
            c = canvas.Canvas(buf, pagesize=(left + size[0], bottom + size[1]))
            c.setFont("Helvetica", FOOTER_SIZE)
            c.setFillColor(colors.HexColor(FOOTER_COLOR))
            c.drawCentredString(left + size[0] / 2.0, bottom + FOOTER_BOTTOM, text)
            c.save()
            cache[key] = PdfReader(BytesIO(buf.getvalue())).pages[0]
        page.merge_page(cache[key])
        writer.add_page(page)
    out = BytesIO()
    writer.write(out)
    return out.getvalue()


def _cover(m: dict[str, Any], drawings: int, failed: list[dict[str, str]]) -> bytes:
    st = getSampleStyleSheet()
    story = [_para(f"Design review: {m.get('assembly', '')}", st["Title"]),
             _para(m.get("description") or "", st["Heading2"]),
             _para(f"Created {m.get('created', '')} by {m.get('user', '')}", st["Normal"]),
             _para(f"Vault state {m.get('state', '')}, version {m.get('version', '')}", st["Normal"]),
             _para(f"Drawings included: {drawings}", st["Normal"]),
             Spacer(1, 12),
             _para("Missing drawings", st["Heading3"])]
    missing = m.get("missing") or []
    story += [_para(x, st["Normal"]) for x in missing] or [_para("None", st["Normal"])]
    story += [Spacer(1, 12), _para("Could not export", st["Heading3"])]
    story += [_para(f"{f.get('drawing', '')}: {f.get('reason', '')}", st["Normal"]) for f in failed] \
        or [_para("None", st["Normal"])]
    story += [Spacer(1, 12), _para("No vendor drawing", st["Heading3"])]
    story += [_para(x, st["Normal"]) for x in (m.get("no_vendor_drawing") or [])] or [_para("None", st["Normal"])]
    return _doc(story)


def _purchased_title(p: dict[str, Any]) -> str:
    vendor = str(p.get("vendor") or "").strip()
    number = str(p.get("vendor_number") or "").strip()
    short = "McMaster" if "MCMASTER" in vendor.upper() else vendor
    detail = " ".join(x for x in (short, number) if x)
    return f"Purchased: {p.get('part_number', '')}" + (f" ({detail})" if detail else "")


def bom_table_data(bom: str, no_vendor: set[str] | None = None) -> tuple[list[list[str]], list[int], list[int]]:
    """BOM table rows (header first) and the table row indices to paint yellow.

    Yellow marks parts we make that are not released: BOM Structure Normal and State not
    Released (blank included). Purchased, Phantom, Reference and Content Center rows are
    never flagged (Zak, 2026-10-13). Orange marks purchased rows whose Part Number has no
    vendor drawing in the package (`no_vendor`); orange wins over yellow.
    """
    df = bp.read_bom_file(bom).fillna("")
    cols = [c for c in BOM_COLUMNS if c in df.columns]
    data = [cols] + [[str(row[c]) for c in cols] for _, row in df.iterrows()]
    yellow: list[int] = []
    if "State" in df.columns:
        def normal(row) -> bool:
            return "BOM Structure" not in df.columns or str(row["BOM Structure"]).strip().lower() == "normal"
        yellow = [i for i, (_, row) in enumerate(df.iterrows(), start=1)
                  if normal(row) and not bp.is_released(row["State"])]
    orange: list[int] = []
    if no_vendor and "Part Number" in df.columns:
        wanted = {str(x).strip().lower() for x in no_vendor if str(x).strip()}
        orange = [i for i, (_, row) in enumerate(df.iterrows(), start=1)
                  if str(row["Part Number"]).strip().lower() in wanted]
        yellow = [r for r in yellow if r not in orange]
    return data, yellow, orange


# Relative column weights; the table is scaled to the page's frame width (landscape letter, 0.5 in margins).
_COL_WEIGHTS = {"Item": 4, "Part Number": 20, "Description": 30, "QTY": 5, "BOM Structure": 10,
                "Material": 11, "REV": 5, "State": 11}
_FRAME_WIDTH = landscape(letter)[0] - 72


def _bom_table(bom: str, no_vendor: set[str] | None = None) -> bytes:
    data, yellow, orange = bom_table_data(bom, no_vendor)
    cols = data[0]
    total = sum(_COL_WEIGHTS.get(c, 10) for c in cols)
    widths = [_FRAME_WIDTH * _COL_WEIGHTS.get(c, 10) / total for c in cols]
    cell = ParagraphStyle("bomcell", fontName="Helvetica", fontSize=8, leading=9.5)
    head = ParagraphStyle("bomhead", parent=cell, fontName="Helvetica-Bold", textColor=colors.white)
    # Paragraph cells wrap long part numbers and descriptions inside their column.
    rows = [[_para(v, head) for v in data[0]]] + [[_para(v, cell) for v in r] for r in data[1:]]
    table = Table(rows, colWidths=widths, repeatRows=1)
    style = [
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1F3864")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONTSIZE", (0, 0), (-1, -1), 8),
        ("GRID", (0, 0), (-1, -1), 0.25, colors.grey),
    ]
    style += [("BACKGROUND", (0, r), (-1, r), colors.HexColor("#FFF59D")) for r in yellow]
    style += [("BACKGROUND", (0, r), (-1, r), colors.HexColor("#FFCC80")) for r in orange]
    table.setStyle(TableStyle(style))
    story = [Paragraph("Bill of materials", getSampleStyleSheet()["Heading2"])]
    key = bom_key(cols, no_vendor is not None)
    if key:
        swatches = Table([[ "", _para(text, cell)] for _, text in key], colWidths=[14, None], hAlign="LEFT")
        swatches.setStyle(TableStyle(
            [("VALIGN", (0, 0), (-1, -1), "MIDDLE"), ("LEFTPADDING", (1, 0), (1, -1), 6)]
            + [("BACKGROUND", (0, i), (0, i), colors.HexColor(c)) for i, (c, _) in enumerate(key)]
            + [("BOX", (0, i), (0, i), 0.25, colors.grey) for i in range(len(key))]))
        story += [swatches, Spacer(1, 6)]
    return _doc(story + [table])


def bom_key(cols: list[str], review_mode: bool) -> list[tuple[str, str]]:
    """Colour key shown above the BOM table: (fill, meaning) for each colour the table can use."""
    key: list[tuple[str, str]] = []
    if "State" in cols:
        key.append(("#FFF59D", "Part we make (BOM Structure Normal) that is not Released"))
    if review_mode and "Part Number" in cols:
        key.append(("#FFCC80", "Purchased part with no vendor drawing in this package"))
    return key


def run_review(bom: str, manifest: str, out_pdf: str) -> dict[str, Any]:
    m = json.loads(Path(manifest).read_text(encoding="utf-8"))
    mode = "assembly" if str(m.get("mode") or "review").strip().lower() == "assembly" else "review"
    failed = [dict(f) for f in (m.get("failed") or [])]
    usable = []
    for d in m.get("drawings") or []:
        try:
            usable.append((d, PdfReader(str(d["pdf"]))))
        except Exception as exc:
            failed.append({"drawing": d.get("drawing", ""), "reason": f"PDF unreadable: {exc}"})

    purchased = []
    for p in ([] if mode == "assembly" else (m.get("purchased") or [])):
        try:
            purchased.append((p, PdfReader(str(p["pdf"]))))
        except Exception as exc:
            failed.append({"drawing": f"Purchased: {p.get('part_number', '')}", "reason": f"PDF unreadable: {exc}"})

    writer = PdfWriter()
    marks: list[tuple[str, int]] = []

    def add(title: str, reader: PdfReader) -> None:
        marks.append((title, len(writer.pages)))
        for page in reader.pages:
            writer.add_page(page)

    cover = _assembly_cover(m, usable, failed) if mode == "assembly" else _cover(m, len(usable), failed)
    add("Cover", PdfReader(BytesIO(cover)))
    no_vendor: set[str] = set()
    if mode != "assembly":
        no_vendor = {str(x) for x in (m.get("no_vendor_drawing") or [])}
        loaded = {str(p.get("part_number", "")) for p, _ in purchased}
        no_vendor |= {str(p.get("part_number", "")) for p in (m.get("purchased") or [])} - loaded
    add("BOM", PdfReader(BytesIO(_bom_table(bom, no_vendor))))
    unreleased_pages: set[int] = set()
    unreleased = 0
    for d, reader in usable:
        start = len(writer.pages)
        add(f"{d.get('part_number', '')} - {d.get('drawing', '')}", reader)
        if mode == "assembly" and not _is_released(d):
            unreleased += 1
            unreleased_pages.update(range(start, len(writer.pages)))
    for p, reader in purchased:
        add(_purchased_title(p), reader)

    merged = BytesIO()
    writer.write(merged)
    if mode == "assembly":
        stamped = apply_watermark_to_pages(merged.getvalue(), UNRELEASED_TEXT, unreleased_pages, font_size=120,
                                           color=WATERMARK_COLOR, opacity=WATERMARK_OPACITY)
        footer = f"FOR ASSEMBLY | {m.get('assembly', '')} Rev {m.get('revision') or '-'} | {date.today().isoformat()}"
        stamped = _apply_footer(stamped, footer)
    else:
        stamped = apply_watermark(merged.getvalue(), WATERMARK, font_size=120,
                                  color=WATERMARK_COLOR, opacity=WATERMARK_OPACITY)
    final = PdfWriter(clone_from=PdfReader(BytesIO(stamped)))
    for title, index in marks:
        final.add_outline_item(title, index)
    Path(out_pdf).parent.mkdir(parents=True, exist_ok=True)
    with open(out_pdf, "wb") as fh:
        final.write(fh)
    return {"ok": True, "mode": mode, "unreleased": unreleased, "pdf": str(out_pdf), "pages": len(final.pages), "drawings": len(usable),
            "purchased": len(purchased), "no_vendor_drawing": len(m.get("no_vendor_drawing") or []),
            "missing": len(m.get("missing") or []), "failed": len(failed)}
