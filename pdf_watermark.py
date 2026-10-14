"""
PDF watermarking helper.
Generates a centered (optionally rotated) text overlay with reportlab and merges
it onto every page of a PDF using pypdf.
"""

from io import BytesIO
from typing import Dict, Tuple

from pypdf import PdfReader, PdfWriter, Transformation
from reportlab.lib.colors import Color
from reportlab.pdfgen import canvas


def _hex_to_rgb(hex_color: str) -> Tuple[float, float, float]:
    h = hex_color.lstrip("#")
    if len(h) != 6:
        raise ValueError(f"Expected '#RRGGBB' hex color, got {hex_color!r}")
    return (
        int(h[0:2], 16) / 255.0,
        int(h[2:4], 16) / 255.0,
        int(h[4:6], 16) / 255.0,
    )


def _make_watermark_page(
    text: str,
    width: float,
    height: float,
    *,
    font_size: int,
    color: str,
    opacity: float,
    rotation: float,
) -> bytes:
    """Build a one-page PDF whose only content is the watermark text."""
    r, g, b = _hex_to_rgb(color)
    buf = BytesIO()
    c = canvas.Canvas(buf, pagesize=(width, height))
    c.setFont("Helvetica-Bold", font_size)
    c.setFillColor(Color(r, g, b, alpha=opacity))
    c.translate(width / 2.0, height / 2.0)
    c.rotate(rotation)
    c.drawCentredString(0, -font_size / 3.0, text)
    c.save()
    return buf.getvalue()


def apply_watermark(
    pdf_bytes: bytes,
    text: str,
    *,
    font_size: int = 80,
    color: str = "#888888",
    opacity: float = 0.3,
    rotation: float = 45.0,
) -> bytes:
    """Return a new PDF with the watermark overlaid on every page.

    Watermark pages are cached per page-size so mixed-size PDFs only generate
    one overlay per unique size.
    """
    reader = PdfReader(BytesIO(pdf_bytes))
    writer = PdfWriter()
    cache: Dict[Tuple[float, float], object] = {}

    for page in reader.pages:
        size = (float(page.mediabox.width), float(page.mediabox.height))
        if size not in cache:
            wm_pdf = _make_watermark_page(
                text,
                size[0],
                size[1],
                font_size=font_size,
                color=color,
                opacity=opacity,
                rotation=rotation,
            )
            cache[size] = PdfReader(BytesIO(wm_pdf)).pages[0]
        page.merge_page(cache[size])
        writer.add_page(page)

    out = BytesIO()
    writer.write(out)
    return out.getvalue()


def apply_watermark_to_pages(
    pdf_bytes: bytes,
    text: str,
    page_indices,
    *,
    font_size: int = 80,
    color: str = "#888888",
    opacity: float = 0.3,
    rotation: float = 45.0,
) -> bytes:
    """Like apply_watermark, but only the pages whose 0-based index is in page_indices."""
    wanted = set(page_indices)
    reader = PdfReader(BytesIO(pdf_bytes))
    writer = PdfWriter()
    cache: Dict[Tuple[float, float], object] = {}
    for i, page in enumerate(reader.pages):
        if i in wanted:
            box = page.mediabox
            left, bottom = float(box.left), float(box.bottom)
            size = (float(box.width), float(box.height))
            if size not in cache:
                wm_pdf = _make_watermark_page(
                    text, size[0], size[1],
                    font_size=font_size, color=color, opacity=opacity, rotation=rotation,
                )
                cache[size] = PdfReader(BytesIO(wm_pdf)).pages[0]
            if left or bottom:
                page.merge_transformed_page(cache[size], Transformation().translate(left, bottom))
            else:
                page.merge_page(cache[size])
        writer.add_page(page)
    out = BytesIO()
    writer.write(out)
    return out.getvalue()
