"""Spec cards for purchased parts, and standard dimensions for Content Center
parts. A spec card is the machine-readable sheet attached to a McMaster part in
Vault (<CD>.specs.json); see docs/superpowers/specs/2026-10-09-mcmaster-intake-design.md.
"""
from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any, Optional

SCHEMA_VERSION = 1
STANDARD_SPECS_PATH = Path(__file__).resolve().parent / "standard_specs.json"

REQUIRED_DIMENSIONS: dict[str, tuple[str, ...]] = {
    "screw": ("head_diameter", "head_height", "length"),
    "nut": ("width_across_flats", "height"),
    "t_slot_nut": ("slot_width", "head_width", "head_height", "overall_height"),
    "washer": ("inner_diameter", "outer_diameter", "thickness"),
    "spring_washer": ("inner_diameter", "outer_diameter", "thickness"),
    "pin": ("diameter", "length"),
    "bearing": ("bore", "outer_diameter", "width"),
    "other": (),
}
TOP_LEVEL_KEYS = ("schema", "cd_number", "source", "type", "description", "material",
                  "grade", "finish", "thread", "dimensions", "ratings", "notes")
RATING_KEYS = ("max_temp_c", "proof_load_kn", "hardness")
CD_NUMBER = re.compile(r"^CD-\d{6}$")


def _is_number(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def validate_card(card: Any, *, require_cd_number: bool = False) -> list[str]:
    """Return every problem with a spec card; an empty list means valid."""
    if not isinstance(card, dict):
        return ["spec card must be a JSON object"]
    errors = [f"missing field: {k}" for k in TOP_LEVEL_KEYS if k not in card]
    if errors:
        return errors
    if card["schema"] != SCHEMA_VERSION:
        errors.append(f"schema must be {SCHEMA_VERSION}")
    ctype = card["type"]
    if ctype not in REQUIRED_DIMENSIONS:
        errors.append("type must be one of: " + ", ".join(REQUIRED_DIMENSIONS))
    src = card["source"]
    if not isinstance(src, dict) or not str(src.get("vendor_number") or "").strip():
        errors.append("source.vendor_number is required")
    dims = card["dimensions"]
    if not isinstance(dims, dict):
        errors.append("dimensions must be an object")
    else:
        for key in REQUIRED_DIMENSIONS.get(ctype, ()):
            if dims.get(key) is None:
                errors.append(f"dimensions.{key} is required for type {ctype}")
        for key, val in dims.items():
            if val is not None and not _is_number(val):
                errors.append(f"dimensions.{key} must be a number in mm, got {val!r}")
    thread = card["thread"]
    if thread is not None:
        if not isinstance(thread, dict) or not str(thread.get("designation") or "").strip():
            errors.append("thread.designation must be a non-empty string")
        elif thread.get("length") is not None and not _is_number(thread["length"]):
            errors.append(f"thread.length must be a number in mm, got {thread['length']!r}")
    ratings = card["ratings"]
    if not isinstance(ratings, dict):
        errors.append("ratings must be an object")
    else:
        for key in RATING_KEYS:
            if key not in ratings:
                errors.append(f"missing field: ratings.{key}")
        for key in ("max_temp_c", "proof_load_kn"):
            if ratings.get(key) is not None and not _is_number(ratings[key]):
                errors.append(f"ratings.{key} must be a number, got {ratings[key]!r}")
    if not isinstance(card["notes"], list):
        errors.append("notes must be a list")
    if require_cd_number and not CD_NUMBER.match(str(card["cd_number"] or "")):
        errors.append("cd_number must look like CD-000000")
    return errors


_CC_NAME = re.compile(
    r"^\s*(?P<std>(?:ISO|DIN)\s*\d+(?:-\d+)?)\s+-\s+(?P<size>.+?)\s+-\s+(?P<material>.+?)\s*(?:\.ipt)?\s*$",
    re.IGNORECASE)


def parse_content_center_name(name: str) -> Optional[dict[str, Any]]:
    """'ISO 4762 - M8 x 45 - Steel.ipt' -> standard, nominal, length, material."""
    m = _CC_NAME.match(name or "")
    if not m:
        return None
    std = re.sub(r"\s+", " ", m["std"].upper().replace("ISO", "ISO ").replace("DIN", "DIN ")).strip()
    size = m["size"]
    nums = re.findall(r"\d+(?:\.\d+)?", size)
    if not nums:
        return None
    length = float(nums[1]) if re.search(r"\bx\b", size, re.IGNORECASE) and len(nums) > 1 else None
    return {"standard": std, "nominal": f"M{nums[0]}", "length": length,
            "material": m["material"].strip()}


def load_standard_tables(path: Path = STANDARD_SPECS_PATH) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def card_from_standard(file_name: str, tables: Optional[dict[str, Any]] = None) -> Optional[dict[str, Any]]:
    """Build a spec card for a Content Center file from the standard tables,
    or None when the family or size is not in the table."""
    parsed = parse_content_center_name(file_name)
    if parsed is None:
        return None
    tables = tables or load_standard_tables()
    std = tables.get("aliases", {}).get(parsed["standard"], parsed["standard"])
    family = tables["families"].get(std)
    if family is None:
        return None
    dims = family["sizes"].get(parsed["nominal"])
    if dims is None:
        return None
    dims = dict(dims)
    if family["type"] == "screw":
        dims["length"] = parsed["length"]
    thread = None
    if family["threaded"]:
        pitch = tables["pitch"].get(parsed["nominal"])
        thread = {"designation": f"{parsed['nominal']}x{pitch:g}" if pitch else parsed["nominal"],
                  "length": parsed["length"] if family["type"] == "screw" else dims.get("height"),
                  "through": family["type"] == "nut"}
    return {
        "schema": SCHEMA_VERSION, "cd_number": None, "source": {"standard": std},
        "type": family["type"], "description": f"{std} {parsed['nominal']}",
        "material": parsed["material"], "grade": None, "finish": None,
        "thread": thread, "dimensions": dims,
        "ratings": {k: None for k in RATING_KEYS},
        "notes": ["Nominal values from the standard table, not a vendor drawing."],
    }
