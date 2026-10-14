# tests/test_part_specs.py
import copy

import pytest

import part_specs as ps

CARD = {
    "schema": 1,
    "cd_number": None,
    "source": {"vendor": "McMASTER-CARR", "vendor_number": "90510A313",
               "pdf": None, "read_on": "2026-10-09"},
    "type": "t_slot_nut",
    "description": "Fully Threaded T-Slot Nut",
    "material": "Steel", "grade": None, "finish": "Black oxide",
    "thread": {"designation": "M8x1.25", "length": 18, "through": True},
    "dimensions": {"slot_width": 16, "head_width": 25, "head_height": 9,
                   "overall_height": 18, "length": 25},
    "ratings": {"max_temp_c": None, "proof_load_kn": None, "hardness": None},
    "notes": [],
}


def card(**changes):
    c = copy.deepcopy(CARD)
    c.update(changes)
    return c


def test_valid_card_has_no_errors():
    assert ps.validate_card(CARD) == []


def test_missing_top_level_field_is_named():
    c = card()
    del c["grade"]
    assert "missing field: grade" in ps.validate_card(c)


def test_dimension_given_as_text_is_rejected_by_name():
    c = card(dimensions={**CARD["dimensions"], "slot_width": "16 mm"})
    errs = ps.validate_card(c)
    assert any("dimensions.slot_width" in e and "number" in e for e in errs)


def test_required_dimension_for_type():
    dims = dict(CARD["dimensions"])
    del dims["head_height"]
    errs = ps.validate_card(card(dimensions=dims))
    assert "dimensions.head_height is required for type t_slot_nut" in errs


def test_unknown_type_rejected():
    assert any(e.startswith("type must be one of") for e in ps.validate_card(card(type="widget")))


def test_other_type_needs_no_dimensions():
    assert ps.validate_card(card(type="other", dimensions={})) == []


def test_thread_may_be_null_but_not_blank():
    assert ps.validate_card(card(thread=None)) == []
    assert "thread.designation must be a non-empty string" in ps.validate_card(
        card(thread={"designation": " ", "length": None, "through": None}))


def test_require_cd_number():
    assert "cd_number must look like CD-000000" in ps.validate_card(CARD, require_cd_number=True)
    assert ps.validate_card(card(cd_number="CD-001888"), require_cd_number=True) == []


def test_not_an_object():
    assert ps.validate_card([]) == ["spec card must be a JSON object"]


@pytest.mark.parametrize("name,expected", [
    ("ISO 4762 - M8 x 45 - Steel.ipt", {"standard": "ISO 4762", "nominal": "M8", "length": 45.0, "material": "Steel"}),
    ("ISO 7089 - 8 - Stainless Steel", {"standard": "ISO 7089", "nominal": "M8", "length": None, "material": "Stainless Steel"}),
    ("DIN 127 - A 8 - Stainless Steel.ipt", {"standard": "DIN 127", "nominal": "M8", "length": None, "material": "Stainless Steel"}),
    ("ISO 7093-1 - 8 - Steel.ipt", {"standard": "ISO 7093-1", "nominal": "M8", "length": None, "material": "Steel"}),
])
def test_parse_content_center_name(name, expected):
    assert ps.parse_content_center_name(name) == expected


def test_parse_rejects_non_standard_names():
    assert ps.parse_content_center_name("Vibration-Resistant-Spring Lock-Washers-5_16_, M8-Steel.ipt") is None
    assert ps.parse_content_center_name("CD-001888.ipt") is None


def test_card_from_standard_screw():
    c = ps.card_from_standard("ISO 4762 - M8 x 30 - Steel.ipt")
    assert c["type"] == "screw"
    assert c["thread"]["designation"] == "M8x1.25"
    assert c["dimensions"] == {"head_diameter": 13, "head_height": 8, "socket": 6, "length": 30.0}
    assert c["source"] == {"standard": "ISO 4762"}


def test_card_from_standard_washer_aliases_7093():
    c = ps.card_from_standard("ISO 7093 - 8 - Steel.ipt")
    assert c["type"] == "washer"
    assert c["dimensions"] == {"inner_diameter": 8.4, "outer_diameter": 24, "thickness": 2}
    assert c["thread"] is None


def test_card_from_standard_unknown_family_or_size():
    assert ps.card_from_standard("ISO 9999 - M8 - Steel.ipt") is None
    assert ps.card_from_standard("ISO 4762 - M64 x 200 - Steel.ipt") is None


def test_card_from_standard_fine_pitch_screw():
    c = ps.card_from_standard("ISO 4762 - M8 x 1 x 30 - Steel.ipt")
    assert c["thread"]["designation"] == "M8x1"
    assert c["thread"]["length"] == 30.0
    assert c["dimensions"]["length"] == 30.0
    assert ps.parse_content_center_name("ISO 4762 - M8 x 1 x 30 - Steel.ipt")["pitch"] == 1.0
