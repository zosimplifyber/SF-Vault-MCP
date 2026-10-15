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

import pandas as pd

import bom_purchasing as bp
from pdf_watermark import apply_watermark, apply_watermark_to_pages

WATERMARK = "DRAFT"
WATERMARK_COLOR = "#C62828"
WATERMARK_OPACITY = 0.25
UNRELEASED_TEXT = "NOT RELEASED"
FOOTER_COLOR = "#666666"
FOOTER_SIZE = 7
FOOTER_BOTTOM = 18
BOM_COLUMNS = ["Item", "Part Number", "Description", "QTY", "Total QTY", "BOM Structure", "Material", "REV", "State", "Notes"]


def _doc(story) -> bytes:
    buf = BytesIO()
    SimpleDocTemplate(buf, pagesize=landscape(letter), leftMargin=36, rightMargin=36,
                      topMargin=36, bottomMargin=36).build(story)
    return buf.getvalue()


def _para(text: Any, style) -> Paragraph:
    # Paragraph parses markup, so every interpolated value must be escaped.
    return Paragraph(escape(str(text)), style)


def _is_released(d: dict[str, Any]) -> bool:
    return bp.is_released(str(d.get("state") or ""))


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
    df = bp.read_bom_file(bom)
    if {"Item", "QTY"} <= set(df.columns):
        # QTY is per ONE of the parent; Total QTY multiplies in every ancestor's.
        ext = bp.extended_quantities(pd.DataFrame({"Row Order": df["Item"], "Item Qty": df["QTY"]}))
        df["Total QTY"] = [f"{q:g}" for q in ext]
    df = df.fillna("")
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
_COL_WEIGHTS = {"Item": 4, "Part Number": 18, "Description": 26, "QTY": 5, "Total QTY": 6, "BOM Structure": 9,
                "Material": 10, "REV": 5, "State": 10, "Notes": 24}
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


NO_VENDOR_LABEL = "(no vendor)"
_EXCLUDED_STRUCTURES = {"phantom", "reference"}


def _text(value: Any) -> str:
    return "" if value is None or (isinstance(value, float) and pd.isna(value)) else str(value).strip()


def _money(value: float) -> str:
    return f"${value:,.2f}"


def _total_quantities(df: pd.DataFrame, children: dict[int, list[int]]) -> list[float]:
    """Total quantity of each row in the assembly: its own QTY times every parent's."""
    return bp.extended_quantities(df, children)


def _reference_problem(info: dict[str, Any]) -> str | None:
    """Why the price list was not used, or None when it was."""
    if str(info.get("reference_source") or "none") in ("mslist", "excel"):
        return None
    if info.get("reference_signin_needed"):
        return "not signed in to the purchasing Microsoft List"
    return "no purchasing list found"


def budget_data(bom: str, sales_tax_rate: float = bp.DEFAULT_SALES_TAX_RATE) -> dict[str, Any]:
    """Budget estimate for a BOM file, priced with the Purchasing Sheet's own rules.

    Purchased rows (Source Buy/Other, as the sheet treats them) are priced: file Cost
    wins over the list Cost Per, shipping is 0, tax is cost x qty x ``sales_tax_rate``,
    and a tariffed row (HS/HTS code) uses the entered Tax/Tariff, never the rate.
    Phantom and Reference rows are left out; Make rows are counted, not priced.
    A price list that cannot be loaded never fails this: file prices still count.
    """
    from vault_tools_cli import reference_note  # lazy: that module imports this one lazily too

    raw = bp.read_bom_file(bom)
    structure = None
    for col in ("BOM Structure", "Source"):
        if col in raw.columns:
            structure = [_text(v).lower() for v in raw[col]]
            break
    unavailable: str | None = None
    note = ""
    try:
        df, err, info = bp.price_bom_dataframe(raw, fill_state=False)
        if err:
            raise ValueError(err)
        unavailable = _reference_problem(info)
        source = str(info.get("reference_source") or "none")
        note = reference_note(source if source in ("mslist", "excel") else "none",
                              bool(info.get("reference_signin_needed")))
    except Exception as exc:  # the review package never fails for budget reasons
        unavailable = f"{type(exc).__name__}: {exc}"
        df, err = bp.coerce_bom_dataframe(raw)
        if err:
            raise ValueError(err) from exc
        bp.apply_price_source(df, df["Cost"].copy() if "Cost" in df.columns else None)
    if unavailable:
        note = ""

    df = df.reset_index(drop=True)
    children = bp.build_children_map(df)
    totals = _total_quantities(df, children)
    lines: list[dict[str, Any]] = []
    unpriced: list[dict[str, str]] = []
    make = file_n = list_n = tariff_missing = 0
    for i, row in df.iterrows():
        kind = structure[i] if structure and i < len(structure) else ""
        if children.get(i) or kind in _EXCLUDED_STRUCTURES:
            continue  # assemblies roll up their children; phantom/reference never cost anything
        if _text(row.get("Source")) not in ("Buy", "Other"):
            make += 1
            continue
        number = _text(row.get("Number"))
        desc = _text(row.get("Description (Item,CO)"))
        cost = bp.parse_money(row.get("Cost Per"))
        if cost != cost or cost <= 0:
            unpriced.append({"part_number": number, "description": desc,
                             "vendor": _text(row.get("Vendor")), "vendor_number": _text(row.get("Vendor Number"))})
            continue
        qty = totals[i]
        subtotal = float(cost) * qty
        tax = tariff = 0.0
        if _text(row.get("HS/HTS Code")):
            entered = bp.parse_money(row.get("Tax/Tariff"))
            if entered == entered:
                tariff = float(entered)
            else:
                tariff_missing += 1
        else:
            tax = subtotal * sales_tax_rate
        src = _text(row.get("Price source"))
        file_n += src == "File"
        list_n += src == "List"
        lines.append({"part_number": number, "description": desc, "vendor": _text(row.get("Vendor")) or NO_VENDOR_LABEL,
                      "qty": qty, "cost": float(cost), "subtotal": subtotal, "tax": tax, "tariff": tariff, "source": src})
    vendors: dict[str, dict[str, Any]] = {}
    for ln in lines:
        v = vendors.setdefault(ln["vendor"], {"vendor": ln["vendor"], "lines": 0, "subtotal": 0.0, "tax": 0.0})
        v["lines"] += 1
        v["subtotal"] += ln["subtotal"]
        v["tax"] += ln["tax"] + ln["tariff"]
    vendor_rows = sorted(vendors.values(), key=lambda v: (-(v["subtotal"] + v["tax"]), v["vendor"].lower()))
    subtotal = sum(ln["subtotal"] for ln in lines)
    sales_tax = sum(ln["tax"] for ln in lines)
    tariffs = sum(ln["tariff"] for ln in lines)
    return {"subtotal": subtotal, "sales_tax": sales_tax, "tariffs": tariffs, "tariff_missing": tariff_missing,
            "total": subtotal + sales_tax + tariffs, "lines": lines, "vendors": vendor_rows,
            "unpriced": unpriced, "make": make, "file": file_n, "list": list_n,
            "rate": sales_tax_rate, "unavailable": unavailable, "note": note}


def _budget_page(b: dict[str, Any]) -> bytes:
    st = getSampleStyleSheet()
    cell = ParagraphStyle("budcell", fontName="Helvetica", fontSize=8, leading=9.5)
    head = ParagraphStyle("budhead", parent=cell, fontName="Helvetica-Bold", textColor=colors.white)
    right = ParagraphStyle("budright", parent=cell, alignment=2)
    right_head = ParagraphStyle("budrighthead", parent=head, alignment=2)

    def table(header: list[str], rows: list[list[str]], weights: list[int], numeric: set[int]) -> Table:
        total = sum(weights)
        data = [[_para(h, right_head if i in numeric else head) for i, h in enumerate(header)]]
        data += [[_para(v, right if i in numeric else cell) for i, v in enumerate(r)] for r in rows]
        t = Table(data, colWidths=[_FRAME_WIDTH * w / total for w in weights], repeatRows=1)
        t.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"),
                               ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1F3864")),
                               ("GRID", (0, 0), (-1, -1), 0.25, colors.grey)]))
        return t

    pct = f"{b['rate'] * 100:g}%"
    tariff_text = _money(b["tariffs"]) + (f" ({b['tariff_missing']} not entered)" if b["tariff_missing"] else "")
    summary = [["Purchased parts subtotal", _money(b["subtotal"])],
               [f"Sales tax ({pct})", _money(b["sales_tax"])],
               ["Tariffs", tariff_text],
               ["Estimated total", _money(b["total"])]]
    sumt = table(["Summary", "Amount"], summary, [60, 40], {1})
    sumt.setStyle(TableStyle([("BACKGROUND", (0, 4), (-1, 4), colors.HexColor("#E8EEF7"))]))
    story: list[Any] = [Paragraph("Budget estimate", st["Heading2"]), sumt, Spacer(1, 6)]
    facts = []
    if b["unavailable"]:
        facts.append(f"Price list unavailable: {b['unavailable']}")
    if b["unpriced"]:
        facts.append(f"Partial: {len(b['unpriced'])} purchased parts have no price")
    if b["tariff_missing"]:
        facts.append(f"Tariff not entered: {b['tariff_missing']} tariffed parts; their price is in the subtotal but their tariff is not, so the total is low")
    facts.append(f"Make parts: {b['make']} (not priced)")
    facts.append(f"Prices: File {b['file']} / List {b['list']}")
    if b["note"]:
        facts.append(b["note"])
    story += [_para(f, cell) for f in facts]

    story += [Spacer(1, 10), Paragraph("Breakdown by vendor", st["Heading3"])]
    story.append(table(["Vendor", "Lines", "Subtotal", "Tax/Tariff", "Total"],
                       [[v["vendor"], str(v["lines"]), _money(v["subtotal"]), _money(v["tax"]),
                         _money(v["subtotal"] + v["tax"])] for v in b["vendors"]]
                       or [["None", "", "", "", ""]], [40, 10, 17, 16, 17], {1, 2, 3, 4}))

    top = sorted(b["lines"], key=lambda ln: -ln["subtotal"])[:10]
    story += [Spacer(1, 10), Paragraph("Top 10 cost drivers", st["Heading3"])]
    story.append(table(["Part Number", "Description", "Qty", "Cost Per", "Line total", "Price source"],
                       [[ln["part_number"], ln["description"], f"{ln['qty']:g}", _money(ln["cost"]),
                         _money(ln["subtotal"]), ln["source"]] for ln in top]
                       or [["None", "", "", "", "", ""]], [20, 38, 7, 11, 12, 12], {2, 3, 4}))

    story += [Spacer(1, 10), Paragraph("Not priced", st["Heading3"])]
    story.append(table(["Part Number", "Description", "Vendor", "Vendor Number"],
                       [[u["part_number"], u["description"], u["vendor"], u["vendor_number"]] for u in b["unpriced"]]
                       or [["None", "", "", ""]], [22, 38, 20, 20], set()))
    return _doc(story)


def run_review(bom: str, manifest: str, out_pdf: str,
               sales_tax_rate: float = bp.DEFAULT_SALES_TAX_RATE) -> dict[str, Any]:
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
    budget: dict[str, Any] | None = None
    if mode != "assembly":
        try:
            budget = budget_data(bom, sales_tax_rate)
            add("Budget estimate", PdfReader(BytesIO(_budget_page(budget))))
        except Exception as exc:  # never fail the review package for budget reasons
            budget = None
            failed.append({"drawing": "Budget estimate", "reason": f"{type(exc).__name__}: {exc}"})
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
    result = {"ok": True, "mode": mode, "unreleased": unreleased, "pdf": str(out_pdf), "pages": len(final.pages), "drawings": len(usable),
              "purchased": len(purchased), "no_vendor_drawing": len(m.get("no_vendor_drawing") or []),
              "missing": len(m.get("missing") or []), "failed": len(failed)}
    if budget is not None:
        result["budget_total"] = round(budget["total"], 2)
        result["unpriced"] = len(budget["unpriced"])
    return result
