"""Design Review budget estimate page: Purchasing Sheet price rules over the review BOM."""
import json
from pathlib import Path

import pandas as pd
import pytest
from pypdf import PdfReader

import bom_purchasing as bp
import review_package as rp
import vault_tools_cli as cli

HEADER = ["Item", "Part Number", "BOM Structure", "QTY", "Description", "File Name",
          "Vendor", "Vendor Number", "Cost", "HS/HTS Code"]


def _bom(path: Path, rows) -> Path:
    lines = ["\t".join(HEADER)]
    for r in rows:
        r = {"BOM Structure": "Purchased", "QTY": "1", "Vendor": "", "Vendor Number": "", "Cost": "",
             "HS/HTS Code": "", "Description": "d", **r}
        r.setdefault("File Name", r["Part Number"] + ".ipt")
        lines.append("\t".join(str(r.get(c, "")) for c in HEADER))
    path.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
    return path


def _ref(monkeypatch, **cols):
    ref = pd.DataFrame(cols)

    def enrich(df, reference_path="", info=None):
        info["source"] = "excel"
        info["signin_needed"] = False
        df, matched, total = bp.lookup_purchased_data(df, ref)
        return df, matched, total, [], []

    monkeypatch.setattr(bp, "_enrich_with_reference", enrich)


def _no_ref(monkeypatch, signin=False):
    def enrich(df, reference_path="", info=None):
        info["source"] = "none"
        info["signin_needed"] = signin
        return df, 0, 0, [], ["No purchasing reference available"]

    monkeypatch.setattr(bp, "_enrich_with_reference", enrich)


def test_mixed_file_and_list_prices_with_default_tax(tmp_path, monkeypatch):
    _ref(monkeypatch, Number=["B"], Vendor=["Acme"], **{"Cost Per": [10.0]})
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [
        {"Item": "1", "Part Number": "A", "QTY": "2", "Cost": "5"},
        {"Item": "2", "Part Number": "B", "QTY": "3"},
    ])))
    assert b["subtotal"] == pytest.approx(10 + 30)
    assert b["sales_tax"] == pytest.approx(40 * 0.07)
    assert b["total"] == pytest.approx(40 * 1.07)
    assert (b["file"], b["list"]) == (1, 1)


def test_custom_tax_rate_and_file_cost_wins(tmp_path, monkeypatch):
    _ref(monkeypatch, Number=["A"], Vendor=["V"], **{"Cost Per": [99.0]})
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [{"Item": "1", "Part Number": "A", "Cost": "4"}])), 0.1)
    assert b["subtotal"] == 4 and b["sales_tax"] == pytest.approx(0.4)


def test_tariffed_row_with_and_without_entered_value(tmp_path, monkeypatch):
    _ref(monkeypatch, Number=["T1", "T2"], Vendor=["V", "V"], **{"Cost Per": [10.0, 20.0],
         "HS/HTS Code": ["7318.15", "7318.15"], "Tax/Tariff": [12.5, None]})
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [
        {"Item": "1", "Part Number": "T1", "QTY": "2"},
        {"Item": "2", "Part Number": "T2"},
    ])))
    assert b["sales_tax"] == 0
    assert b["tariffs"] == 12.5
    assert b["tariff_missing"] == 1
    assert b["subtotal"] == pytest.approx(40)
    assert b["total"] == pytest.approx(52.5)


def test_assembly_quantities_multiply_down_the_tree(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [
        {"Item": "-", "Part Number": "TOP", "BOM Structure": "Normal", "QTY": "-"},
        {"Item": "1", "Part Number": "SUB", "BOM Structure": "Normal", "QTY": "2"},
        {"Item": "1.1", "Part Number": "BOLT", "QTY": "4", "Cost": "1"},
        {"Item": "2", "Part Number": "NUT", "QTY": "3", "Cost": "1"},
    ])))
    qty = {ln["part_number"]: ln["qty"] for ln in b["lines"]}
    assert qty == {"BOLT": 8, "NUT": 3}
    assert b["subtotal"] == 11


def test_make_counted_phantom_and_reference_excluded(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [
        {"Item": "1", "Part Number": "M", "BOM Structure": "Normal", "Cost": "50"},
        {"Item": "2", "Part Number": "PH", "BOM Structure": "Phantom", "Cost": "50"},
        {"Item": "3", "Part Number": "RF", "BOM Structure": "Reference", "Cost": "50"},
        {"Item": "4", "Part Number": "P", "Cost": "2"},
    ])))
    assert b["make"] == 1
    assert b["subtotal"] == 2 and len(b["unpriced"]) == 0


def test_unpriced_list_and_vendor_ordering(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    b = rp.budget_data(str(_bom(tmp_path / "b.txt", [
        {"Item": "1", "Part Number": "A", "Vendor": "Small", "Cost": "1"},
        {"Item": "2", "Part Number": "B", "Vendor": "Big", "Cost": "100"},
        {"Item": "3", "Part Number": "C", "Vendor": "Mid", "Cost": "10"},
        {"Item": "4", "Part Number": "D", "Vendor": "Nope", "Vendor Number": "N-1", "Description": "mystery"},
    ])))
    assert [v["vendor"] for v in b["vendors"]] == ["Big", "Mid", "Small"]
    assert b["unpriced"] == [{"part_number": "D", "description": "mystery", "vendor": "Nope", "vendor_number": "N-1"}]


def _review(tmp_path, bom, **kw):
    m = {"assembly": "ASM", "description": "x", "user": "u", "state": "WIP", "version": "1",
         "created": "2026-10-13", "drawings": [], "missing": [], "failed": [], **kw}
    mp = tmp_path / "m.json"
    mp.write_text(json.dumps(m), encoding="utf-8")
    out = tmp_path / "out.pdf"
    return rp.run_review(str(bom), str(mp), str(out)), out


def test_review_page_text_bookmark_and_result_json(tmp_path, monkeypatch):
    _no_ref(monkeypatch, signin=True)
    bom = _bom(tmp_path / "b.txt", [
        {"Item": "1", "Part Number": "A", "Vendor": "Acme", "Cost": "10", "QTY": "2", "Description": "Widget"},
        {"Item": "2", "Part Number": "NOPRICE", "Description": "Gizmo", "Vendor Number": "G-9"},
        {"Item": "3", "Part Number": "MK", "BOM Structure": "Normal"},
    ])
    res, out = _review(tmp_path, bom)
    assert res["ok"] and res["budget_total"] == 21.4 and res["unpriced"] == 1
    reader = PdfReader(str(out))
    assert [o.title for o in reader.outline] == ["Cover", "BOM", "Budget estimate"]
    text = "\n".join(p.extract_text() or "" for p in reader.pages)
    for needle in ("Budget estimate", "Purchased parts subtotal", "Sales tax (7%)", "Estimated total", "$21.40",
                   "Partial: 1 purchased parts have no price", "Make parts: 1 (not priced)", "Prices: File 1 / List 0",
                   "Price list unavailable: not signed in", "Breakdown by vendor", "Top 10 cost drivers",
                   "Not priced", "Gizmo", "G-9"):
        assert needle in text, needle


def test_reference_failure_still_builds_the_page(tmp_path, monkeypatch):
    def boom(df, reference_path="", info=None):
        raise RuntimeError("list exploded")

    monkeypatch.setattr(bp, "_enrich_with_reference", boom)
    bom = _bom(tmp_path / "b.txt", [{"Item": "1", "Part Number": "A", "Cost": "10"}])
    res, out = _review(tmp_path, bom)
    assert res["ok"] and res["budget_total"] == 10.7 and res["failed"] == 0
    text = "\n".join(p.extract_text() or "" for p in PdfReader(str(out)).pages)
    assert "Price list unavailable: RuntimeError: list exploded" in text


def test_sales_tax_rate_reaches_the_page(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    bom = _bom(tmp_path / "b.txt", [{"Item": "1", "Part Number": "A", "Cost": "100"}])
    m = tmp_path / "m.json"
    m.write_text(json.dumps({"assembly": "A", "drawings": []}), encoding="utf-8")
    res = rp.run_review(str(bom), str(m), str(tmp_path / "o.pdf"), 0.0825)
    assert res["budget_total"] == 108.25


def test_cli_review_accepts_sales_tax_rate(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    bom = _bom(tmp_path / "b.txt", [{"Item": "1", "Part Number": "A", "Cost": "100"}])
    m = tmp_path / "m.json"
    m.write_text(json.dumps({"assembly": "A", "drawings": []}), encoding="utf-8")
    r = tmp_path / "r.json"
    code = cli.main(["review", "--bom", str(bom), "--manifest", str(m), "--out", str(tmp_path / "o.pdf"),
                     "--result", str(r), "--sales-tax-rate", "0.0825"])
    assert code == 0 and json.loads(r.read_text(encoding="utf-8"))["budget_total"] == 108.25


def test_assembly_mode_has_no_budget_page(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    bom = _bom(tmp_path / "b.txt", [{"Item": "1", "Part Number": "A", "Cost": "10"}])
    res, out = _review(tmp_path, bom, mode="assembly", revision="1")
    assert res["ok"] and "budget_total" not in res
    assert [o.title for o in PdfReader(str(out)).outline] == ["Cover", "BOM"]


def test_long_unpriced_list_wraps_onto_more_pages(tmp_path, monkeypatch):
    _no_ref(monkeypatch)
    bom = _bom(tmp_path / "b.txt", [{"Item": str(i), "Part Number": f"P{i}"} for i in range(1, 121)])
    res, out = _review(tmp_path, bom)
    reader = PdfReader(str(out))
    assert res["unpriced"] == 120
    text = "\n".join(p.extract_text() or "" for p in reader.pages)
    assert "P120" in text and res["pages"] > 4
