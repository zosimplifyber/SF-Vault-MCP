"""McMaster part intake: file a hand-downloaded McMaster STEP + PDF into Vault
as a numbered, categorized Inventor part with a spec card attached, and serve
spec cards back for parts and assemblies.

Spec: docs/superpowers/specs/2026-10-09-mcmaster-intake-design.md
"""
from __future__ import annotations

import hashlib
import re
import secrets
import time
from pathlib import Path
from typing import Any, Callable, Optional

LIBRARY_ROOT = "$/DESIGNS/Simplifyber Library/McMaster-Carr"
DONE_FOLDER = "McMaster intake done"
VENDOR = "McMASTER-CARR"
FOLDER_BY_TYPE = {
    "screw": "screws", "nut": "nuts", "t_slot_nut": "nuts", "washer": "washers",
    "spring_washer": "washers", "pin": "pins", "bearing": "bearing",
}
_PN = re.compile(r"^\d{3,5}[A-Z]\d{1,4}$")
_SAFE_FOLDER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _\-]*$")
STEP_SUFFIXES = {".step", ".stp"}


class IntakeError(ValueError):
    """A problem Zak can fix (bad input, missing download)."""


def normalize_pn(pn: str) -> str:
    s = re.sub(r"\s+", "", pn or "").upper()
    if not _PN.match(s):
        raise IntakeError(f"{pn!r} does not look like a McMaster part number (e.g. 90510A313)")
    return s


def find_downloads(pn: str, downloads_dir: Path) -> dict[str, Path]:
    """Newest STEP and PDF directly in downloads_dir whose name contains pn."""
    found: dict[str, list[Path]] = {"step": [], "pdf": []}
    for p in Path(downloads_dir).iterdir():
        if not p.is_file() or pn not in p.name.upper():
            continue
        suffix = p.suffix.lower()
        if suffix in STEP_SUFFIXES:
            found["step"].append(p)
        elif suffix == ".pdf":
            found["pdf"].append(p)
    missing = [k.upper() for k, v in found.items() if not v]
    if missing:
        raise IntakeError(f"No {' or '.join(missing)} for {pn} in {downloads_dir}. Download it from McMaster first.")
    return {k: max(v, key=lambda f: f.stat().st_mtime) for k, v in found.items()}


def sha256_file(path: Path) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def target_folder(card_type: str, folder: str = "") -> str:
    name = folder.strip() or FOLDER_BY_TYPE.get(card_type, "")
    if not name:
        raise IntakeError(f"Type {card_type!r} has no default folder; pass folder=<subfolder name>.")
    if not _SAFE_FOLDER.match(name):
        raise IntakeError(f"Folder name {name!r} must be a single plain sub-folder name.")
    return f"{LIBRARY_ROOT}/{name}"


def local_dir_for(vault_folder: str, workspace_root: Path) -> Path:
    rel = vault_folder.removeprefix("$/")
    return Path(workspace_root, *rel.split("/"))


def build_iproperties(cd_number: str, pn: str, description: str, finish: str) -> dict[str, dict[str, str]]:
    return {
        "design": {"Part Number": cd_number, "Description": description, "Vendor": VENDOR,
                   "Catalog Web Link": pn, "Designer": "Zak O."},
        "custom": {"Vendor Number": pn, "Source": "Buy", "Material Finish": finish,
                   "Engr Approved By": "NOT REVIEWED"},
    }


def file_names(cd_number: str, pn: str) -> dict[str, str]:
    return {"ipt": f"{cd_number}.ipt", "pdf": f"{cd_number} McMaster {pn}.pdf",
            "specs": f"{cd_number}.specs.json"}


class PreviewStore:
    """In-process preview tokens. A token is used once and expires."""

    def __init__(self, ttl_seconds: float = 900, clock: Callable[[], float] = time.monotonic):
        self._ttl = ttl_seconds
        self._clock = clock
        self._items: dict[str, tuple[float, dict[str, Any]]] = {}

    def put(self, payload: dict[str, Any]) -> str:
        token = secrets.token_urlsafe(9)
        self._items[token] = (self._clock(), payload)
        return token

    def take(self, token: str) -> Optional[dict[str, Any]]:
        item = self._items.pop(token, None)
        if item is None or self._clock() - item[0] > self._ttl:
            return None
        return item[1]
