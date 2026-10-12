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
