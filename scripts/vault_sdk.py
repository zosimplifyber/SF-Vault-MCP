"""
Python wrapper around scripts/vault_sdk.ps1 — the PowerShell bridge that
hosts the Vault .NET SDK.

Why this exists
---------------
Vault REST v2 is read-only; the only way to update item / file properties
or change lifecycle states is through the .NET SDK. PowerShell loads the
SDK natively (via Add-Type), so we shell out from Python rather than try
to host .NET inside Python (pythonnet doesn't currently support Python
3.14, which is what this project runs on).

Usage
-----
    from vault_sdk import (
        get_lifecycle_states,
        get_item_property_definitions,
        lookup_item,
        update_item_properties,
        update_item_lifecycle_states,
        update_file_lifecycle_states,
    )

    states = get_lifecycle_states()
    item   = lookup_item("SF-001702")
    update_item_properties([item["id"]], {"EngrApproved": "Yes"})

Performance
-----------
Each call spawns a fresh PowerShell process and signs in to Vault — about
~1-2 seconds. For interactive operations that's fine. For batch use, build
the request list and call once (e.g. update_item_properties(many_ids, ...))
rather than looping in Python.
"""

from __future__ import annotations

import json
import logging
import shutil
import subprocess
from functools import lru_cache
from pathlib import Path
from typing import Any, Iterable, Optional

logger = logging.getLogger(__name__)

PROJECT_ROOT = Path(__file__).resolve().parent.parent
PS_SCRIPT = PROJECT_ROOT / "scripts" / "vault_sdk.ps1"

# Default per-call timeout. Sign-in + SDK load is ~1-2s; allow generous
# headroom for the slow Vault calls (BOM walks, lifecycle changes).
DEFAULT_TIMEOUT_SECONDS = 120


class VaultSDKError(RuntimeError):
    """Raised when the PowerShell bridge returns a non-zero exit code or
    when the JSON response cannot be parsed."""


# ---------------------------------------------------------------------------
# Transport
# ---------------------------------------------------------------------------

def _powershell_exe() -> str:
    """Return the PowerShell executable path. Prefer pwsh (PowerShell 7+),
    which the Vault 2026 SDK assemblies need, else fall back to
    powershell.exe (5.1, always present on Windows)."""
    exe = shutil.which("pwsh")
    if exe:
        return exe
    # A server started before PowerShell 7 was installed has a stale PATH;
    # look in the default install folders before falling back to 5.1, which
    # cannot load the .NET 8 Vault 2026 assemblies.
    for candidate in (
        Path(r"C:\Program Files\PowerShell\7\pwsh.exe"),
        Path.home() / "AppData/Local/Microsoft/WindowsApps/pwsh.exe",
    ):
        if candidate.exists():
            return str(candidate)
    exe = shutil.which("powershell")
    if exe:
        return exe
    # Last resort — Windows always has this absolute path
    return r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"


def _call_ps(operation: str, args: Optional[dict] = None,
             *, timeout: float = DEFAULT_TIMEOUT_SECONDS) -> Any:
    """Invoke vault_sdk.ps1 with one operation, return parsed JSON.

    Raises ``VaultSDKError`` if the script exits non-zero or the response
    isn't valid JSON. Stderr is included in the exception message so the
    caller sees what went wrong inside PowerShell.
    """
    if not PS_SCRIPT.exists():
        raise VaultSDKError(f"PowerShell bridge not found: {PS_SCRIPT}")

    args_json = json.dumps(args or {})
    cmd = [
        _powershell_exe(),
        "-NoProfile",            # skip user profile for speed + reproducibility
        # NOTE: intentionally NOT -NonInteractive — vault_sdk.ps1 uses
        # VdfForms.Library.Login which may pop a Vault sign-in dialog the
        # first time (subsequent runs reuse stored Autodesk Account creds).
        # -STA is required because that dialog is WinForms.
        "-STA",
        "-ExecutionPolicy", "Bypass",
        "-File", str(PS_SCRIPT),
        "-Operation", operation,
        "-ArgsJson", args_json,
    ]
    logger.debug("vault_sdk: %s args=%s", operation, args_json)

    try:
        proc = subprocess.run(
            cmd, capture_output=True, text=True, timeout=timeout,
            encoding="utf-8", errors="replace",
        )
    except subprocess.TimeoutExpired as exc:
        raise VaultSDKError(
            f"{operation} timed out after {timeout}s"
        ) from exc

    if proc.returncode != 0:
        # The PowerShell side writes errors to stderr and exits 1
        msg = (proc.stderr or proc.stdout or "(no output)").strip()
        raise VaultSDKError(f"{operation} failed: {msg}")

    out = (proc.stdout or "").strip()
    if not out:
        raise VaultSDKError(f"{operation}: empty response")
    try:
        return json.loads(out)
    except json.JSONDecodeError as exc:
        raise VaultSDKError(
            f"{operation}: could not parse JSON response: {exc}\n"
            f"raw output: {out[:500]}"
        ) from exc


# ---------------------------------------------------------------------------
# Cached reads — these don't change inside a process lifetime
# ---------------------------------------------------------------------------

@lru_cache(maxsize=1)
def get_lifecycle_states() -> dict[str, Any]:
    """Return every lifecycle definition + its states.

    Shape:
        {
          "definitions": [
            {"id": 7, "name": "Item Release Process",
             "states": [{"id": 27, "name": "Work in Progress"}, ...]},
            ...
          ]
        }
    """
    return _call_ps("GetLifecycleStates")


def get_distinct_state_names() -> list[str]:
    """Convenience: flat sorted list of every distinct state name across
    every lifecycle definition. Used by the workflow GUI's Target State
    dropdown."""
    data = get_lifecycle_states()
    names: set[str] = set()
    for d in data.get("definitions") or []:
        for s in d.get("states") or []:
            n = s.get("name")
            if isinstance(n, str) and n.strip():
                names.add(n.strip())
    return sorted(names, key=str.lower)


def find_state_id(
    target_name: str,
    *,
    definition_id: Optional[int] = None,
    definition_name: Optional[str] = None,
) -> Optional[int]:
    """Return the id of a state matching ``target_name`` (case-insensitive).

    Vault servers typically have multiple lifecycle definitions ("Item
    Release Process", "Basic Release Process", etc.) and EACH defines its
    own "Released" state with a different id. To pick the right one,
    pass ``definition_id`` (preferred — get it from ``lookup_item()``) or
    ``definition_name`` (e.g. ``"Item Release Process"``).

    With no filter, returns the first match across all definitions —
    fine for vaults that only use one lifecycle, but ambiguous otherwise.
    """
    needle = (target_name or "").strip().lower()
    if not needle:
        return None
    data = get_lifecycle_states()
    for d in data.get("definitions") or []:
        if definition_id is not None and int(d.get("id", -1)) != int(definition_id):
            continue
        if definition_name is not None and \
           str(d.get("name", "")).strip().lower() != definition_name.strip().lower():
            continue
        for s in d.get("states") or []:
            if str(s.get("name", "")).strip().lower() == needle:
                try:
                    return int(s["id"])
                except (TypeError, ValueError, KeyError):
                    pass
    return None


def find_state_id_for_item(item: dict[str, Any], target_name: str) -> Optional[int]:
    """Convenience: given the dict returned by ``lookup_item``, find the
    target state id within the item's own lifecycle definition. This is
    what the release workflow should use — it picks the right "Released"
    state automatically based on which lifecycle the item is using.
    """
    def_id = item.get("lifecycleDefId")
    if def_id is None:
        return find_state_id(target_name)
    return find_state_id(target_name, definition_id=int(def_id))


@lru_cache(maxsize=1)
def get_item_property_definitions() -> dict[str, Any]:
    """Return every ITEM-class property definition (id, sysName, dispName,
    dataType, isSystem, isActive). Useful when you need to map between
    the rules-JSON property names and Vault's internal IDs."""
    return _call_ps("GetItemPropertyDefinitions")


@lru_cache(maxsize=1)
def get_item_categories() -> dict[str, Any]:
    """Return every ITEM category (id, name)."""
    return _call_ps("GetItemCategories")


# ---------------------------------------------------------------------------
# Per-item lookup
# ---------------------------------------------------------------------------

def lookup_file(master_id: int | str) -> dict[str, Any]:
    """Look up a file by master id. Returns the file's id, master id,
    name, revision, and lifecycle def + state ids — used by Step 6 of
    the release workflow to figure out which lifecycle "Released" lives
    in for the CAD files attached to a BOM."""
    return _call_ps("LookupFile", {"masterId": int(master_id)})


def find_state_id_for_file(file: dict[str, Any], target_name: str) -> Optional[int]:
    """Convenience: given the dict returned by ``lookup_file``, find the
    target state id within the file's own lifecycle definition."""
    def_id = file.get("lifecycleDefId")
    if not def_id:
        return find_state_id(target_name)
    return find_state_id(target_name, definition_id=int(def_id))


def lookup_item(number: str) -> dict[str, Any]:
    """Look up a single item by part number. Returns:

        {
          "found": True,
          "id": ...,            # item-version id (use for property edits)
          "masterId": ...,      # master id (use for lifecycle changes)
          "number": "SF-001702",
          "title": "...",
          "revision": "2",
          "lifecycleDefId": 7,
          "lifecycleStateId": 29,
          "properties": {"Title (Item,CO)": "...", ...}
        }

    Or ``{"found": False, "number": "..."}`` if the part number doesn't
    match any item.
    """
    return _call_ps("LookupItem", {"number": number})


# ---------------------------------------------------------------------------
# Writes
# ---------------------------------------------------------------------------

def update_item_properties(
    item_ids: Iterable[int | str],
    properties: dict[str, Any],
) -> dict[str, Any]:
    """Set one or more properties on the given item-version IDs.

    ``properties`` keys can be either the system name (e.g. ``"EngrApproved"``)
    or the display name (e.g. ``"Engr Approved"``) — both are accepted.

    Returns ``{"updated": <count>}``.
    """
    ids = [int(i) for i in item_ids]
    if not ids:
        raise ValueError("item_ids must not be empty")
    if not properties:
        raise ValueError("properties must not be empty")
    return _call_ps("UpdateItemProperties", {
        "itemIds": ids,
        "properties": properties,
    })


def update_item_lifecycle_states(
    master_ids: Iterable[int | str],
    state_id: int,
    *,
    comment: str = "",
) -> dict[str, Any]:
    """Promote one or more items (by *master* ID) to ``state_id``.

    Use ``find_state_id("Released")`` to get the right state id, or read
    ``get_lifecycle_states()`` directly. Returns ``{"updated": <count>}``.
    """
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call_ps("UpdateItemLifeCycleStates", {
        "masterIds": ids,
        "stateId": int(state_id),
        "comment": comment,
    })


def update_file_lifecycle_states(
    master_ids: Iterable[int | str],
    state_id: int,
    *,
    comment: str = "",
) -> dict[str, Any]:
    """Promote one or more files (by *master* ID) to ``state_id``."""
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call_ps("UpdateFileLifeCycleStates", {
        "masterIds": ids,
        "stateId": int(state_id),
        "comment": comment,
    })


def update_item_categories(
    master_ids: Iterable[int | str],
    category_id: int,
    *,
    comment: str = "",
) -> dict[str, Any]:
    """Change the Category for one or more items (by *master* ID).

    Use ``get_item_categories()`` to look up the category id you want, or read
    the IDs straight off ``CategoryService.GetCategoriesByEntityClassId('ITEM')``.
    Returns ``{"updated": <count>}``.

    Items must be in 'Work in Progress' for the category change to apply (Vault
    rejects category changes on Released items the same way it rejects property
    edits).
    """
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call_ps("UpdateItemCategories", {
        "masterIds": ids,
        "categoryId": int(category_id),
        "comment": comment,
    })


def get_cad_bom(master_id: int | str) -> dict[str, Any]:
    """Read the BOM Inventor stored in Vault for a file (by master ID).

    Returns ``{"found", "root", "comps", "insts"}``: components with file
    name, type, BOM structure and iProperties, and parent/child links with
    per-parent quantities. Read-only session, so no license seat is used.
    """
    return _call_ps("GetCadBom", {"masterId": int(master_id)}, timeout=600)


def checkout_file(master_id: int | str, *, comment: str = "") -> dict[str, Any]:
    """Check out the latest version of a file (by master ID) without
    downloading it. Returns the file's id, masterId, name and checkout flag."""
    return _call_ps("CheckoutFile", {"masterId": int(master_id), "comment": comment})


def undo_checkout_file(master_id: int | str) -> dict[str, Any]:
    """Undo the checkout on a file (by master ID). Undoing another user's
    checkout needs Vault admin rights and discards their pending check-in."""
    return _call_ps("UndoCheckoutFile", {"masterId": int(master_id)})


def update_file_properties(
    master_ids: Iterable[int | str],
    properties: dict[str, Any],
) -> dict[str, Any]:
    """Set Vault properties on the latest version of each file (by master ID).

    Creates a new property-only version. CAD-mapped properties reach the
    file itself only after an ``Autodesk.Vault.SyncProperties`` job runs.
    Returns ``{"updated": <count>, "files": [{id, masterId, name, ...}]}``.
    """
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    if not properties:
        raise ValueError("properties must not be empty")
    return _call_ps("UpdateFileProperties", {
        "masterIds": ids,
        "properties": properties,
    })


__all__ = [
    "VaultSDKError",
    "get_lifecycle_states",
    "get_distinct_state_names",
    "find_state_id",
    "find_state_id_for_item",
    "get_item_property_definitions",
    "get_item_categories",
    "lookup_item",
    "lookup_file",
    "find_state_id_for_file",
    "update_item_properties",
    "update_item_lifecycle_states",
    "update_file_lifecycle_states",
    "update_item_categories",
    "checkout_file",
    "undo_checkout_file",
    "update_file_properties",
]
