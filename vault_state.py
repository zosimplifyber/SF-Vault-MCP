"""
Vault lifecycle state for BOM part numbers — read-only and best effort.

The purchasing sheet shows each part's Vault state ("Released", "Work in
Progress", …). Every Vault file-search hit already carries that state, so one
keyword search per part number is enough; nothing is written and no CAD file is
touched.

Design notes
------------
* **Best effort, never fatal.** No ``config.json``, no network, bad credentials,
  a Vault outage — every failure returns an empty map plus a warning, and the
  caller leaves the State column blank. Sheet generation must never depend on
  Vault being reachable: the standalone ``.exe`` ships without ``config.json``
  and takes the no-config path on every run.
* ``vault_rest_api`` is imported **lazily**, so the Excel-only / offline path
  costs nothing (same pattern ``purchasing_reference`` uses for ``msal``).
* Vault's search is keyword-based and matches properties as well as names —
  querying "CD-001582" returns "CD-001582.iam" *and* an unrelated "SF-001915".
  ``pick_state_file`` therefore trusts a hit only when the name matches, and
  only for entities that are files. Showing no state beats showing someone
  else's.
* A BOM row's Part Number is the **item** number ("SF-001922"), whose item
  lifecycle state is not the CAD file's. Callers pass the export's ``Filename``
  and ``Title`` columns as aliases so the search hits "CD-001578.ipt" directly.

Reference export for manual checks:
``C:\\Vault Workspace\\DESIGNS\\PRODUCTION EQUIPMENT\\CD-001582 MFG BOM.xlsx``
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable, Iterable

from supplier_pricing.normalize import loose_part_key, normalize_part_number

# Vault caps concurrent work anyway; this keeps a 300-row BOM from opening 300
# sockets at once while still finishing a big BOM in a few seconds.
MAX_CONCURRENCY = 8

# When several files share a part number, the model is the one whose state the
# BOM means. Anything not listed sorts last (files stored without an extension).
_EXT_PRIORITY = (".iam", ".ipt", ".idw", ".dwg")


# --------------------------------------------------------------------------- config

def _project_dir() -> str:
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.dirname(os.path.abspath(__file__))


def vault_config(config_path: str = "") -> dict | None:
    """The `vault` block of config.json, or None when it is absent/incomplete."""
    path = config_path or os.path.join(_project_dir(), "config.json")
    try:
        with open(path, encoding="utf-8") as f:
            cfg = (json.load(f) or {}).get("vault") or {}
    except Exception:      # noqa: BLE001 — missing/unreadable config is normal
        return None
    if not all(cfg.get(k) for k in ("servername", "username", "password", "database")):
        return None
    return cfg


# --------------------------------------------------------------------------- matching

def _stem(name: Any) -> str:
    base = os.path.basename(str(name or "")).strip()
    return normalize_part_number(os.path.splitext(base)[0])


def _full(name: Any) -> str:
    return normalize_part_number(os.path.basename(str(name or "")).strip())


def _ext_rank(name: Any) -> int:
    ext = os.path.splitext(str(name or ""))[1].lower()
    return _EXT_PRIORITY.index(ext) if ext in _EXT_PRIORITY else len(_EXT_PRIORITY)


def is_file_entity(row: dict) -> bool:
    """True for a file hit that can carry a lifecycle state.

    ``search_files`` also returns **items** and **folders**: querying "SF-001922"
    returns the ItemVersion (Work in Progress) while the CAD file CD-001578.ipt
    is Released, and an exact-named folder would otherwise mask the real file.
    """
    entity = str(row.get("entityType") or "").strip().lower()
    if entity and not entity.startswith("file"):
        return False
    if row.get("subfolderCount") is not None or row.get("isLibrary") is not None:
        return False
    return bool(state_of(row))


def _starts_at_boundary(name_key: str, key: str) -> bool:
    """`name_key` is `key` plus a separated suffix ("DIN 934 - M5" + " x 0.8").

    The boundary is what keeps "M6 X 1" from matching "M6 X 10".
    """
    if not key or not name_key.startswith(key):
        return False
    rest = name_key[len(key):]
    return rest[:1] in (" ", "-", "_")


def _best(matches: list[dict]) -> dict | None:
    """The one file to believe, or None when the survivors disagree.

    Extension priority settles the common case (CD-001578.ipt Released beside
    CD-001578_perf.stl Work in Progress). When files of the SAME rank disagree —
    the duplicated library fasteners — no rule can pick correctly, so return
    nothing rather than a coin flip.
    """
    if not matches:
        return None
    ranked = sorted(matches, key=lambda f: _ext_rank(f.get("name")))
    top_rank = _ext_rank(ranked[0].get("name"))
    top = [f for f in ranked if _ext_rank(f.get("name")) == top_rank]
    if len({state_of(f) for f in top}) > 1:
        return None
    return top[0]


def pick_state_file(number: Any, files: Iterable[dict]) -> dict | None:
    """The file this BOM row means, or None.

    ``number`` may be a file name ("CD-001578.ipt"), a CAD number ("CD-001578")
    or a part number. Three passes, each only tried if the previous found
    nothing: exact name, punctuation-insensitive, then a separated prefix.
    Returning None is a valid answer — a keyword-only hit (Vault searches
    properties too) is not this part and must not lend it a state.
    """
    rows = [f for f in files if is_file_entity(f)]
    text = str(number or "").strip()
    key = normalize_part_number(text)
    stem_key = normalize_part_number(os.path.splitext(text)[0])
    if not rows or not key:
        return None

    exact = [f for f in rows
             if _full(f.get("name")) in (key, stem_key)
             or _stem(f.get("name")) in (key, stem_key)]
    if (best := _best(exact)):
        return best

    loose = loose_part_key(stem_key)
    if loose:
        hits = [f for f in rows if loose_part_key(_stem(f.get("name"))) == loose]
        if (best := _best(hits)):
            return best

    # CAD files only on this pass. A longer name that continues with a separator
    # is a library part's full spec ("DIN 934 - M5" -> "DIN 934 - M5 x 0.8.ipt"),
    # but "CD-001582" -> "CD-001582 BOM.xlsx" is a spreadsheet about the part,
    # not the part.
    prefixed = [f for f in rows
                if _ext_rank(f.get("name")) < len(_EXT_PRIORITY)
                and _starts_at_boundary(_stem(f.get("name")), stem_key)]
    return _best(prefixed)


def state_of(file_row: dict) -> str:
    """Lifecycle state name from a file-search hit ('Released', …)."""
    lifecycle = file_row.get("lifecycleState") or {}
    return str(lifecycle.get("name") or file_row.get("state") or "").strip()


# --------------------------------------------------------------------------- fetch

async def _fetch_states(cfg: dict, numbers: list[str],
                        aliases: dict[str, list[str]] | None = None) -> dict[str, str]:
    """Sign in and resolve one state per part number. Raises on sign-in failure.

    ``aliases`` gives better search keys for a part (its CAD file name, then its
    CD- number) — tried in order, the part number last.
    """
    aliases = aliases or {}
    from vault_rest_api import VaultRestAPI      # lazy — see module docstring

    api = VaultRestAPI(servername=cfg["servername"])
    sign_in = await api.create_session(
        database=cfg["database"], username=cfg["username"], password=cfg["password"],
    )
    if sign_in["error"]:
        raise RuntimeError(f"Vault sign-in failed: {str(sign_in['data'])[:200]}")
    data = sign_in["data"] or {}
    vault_id = str((data.get("vaultInformation") or {}).get("id", "")
                   or data.get("vaultId", "") or "")

    sem = asyncio.Semaphore(MAX_CONCURRENCY)

    async def one(number: str) -> tuple[str, str]:
        for candidate in [*aliases.get(normalize_part_number(number), []), number]:
            async with sem:
                result = await api.search_files(vault_id, candidate, limit=25)
            if result["error"]:
                continue
            payload = result["data"] or {}
            rows = payload.get("results") or payload.get("value") or []
            hit = pick_state_file(candidate, rows)
            if hit and state_of(hit):
                return number, state_of(hit)
        return number, ""

    out: dict[str, str] = {}
    for res in await asyncio.gather(*(one(n) for n in numbers), return_exceptions=True):
        if isinstance(res, BaseException):
            continue          # one bad lookup must not sink the rest
        number, state = res
        if state:
            out[normalize_part_number(number)] = state
    return out


def _run(factory: Callable[[], Any]) -> Any:
    """Run a coroutine from sync code, including from inside a running loop.

    The GUI and CLI have no loop; the MCP tools are `async def` and call sheet
    generation directly, where `asyncio.run` would raise. Takes a factory so the
    coroutine is only created on the loop that will run it.
    """
    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return asyncio.run(factory())
    with ThreadPoolExecutor(max_workers=1) as pool:
        return pool.submit(lambda: asyncio.run(factory())).result()


# --------------------------------------------------------------------------- public

def lookup_file_states(
    numbers: Iterable[Any], *, aliases: dict[str, list[str]] | None = None,
    config_path: str = "",
) -> tuple[dict[str, str], list[str]]:
    """Map normalized part number -> Vault state. Returns (states, warnings).

    ``aliases`` maps a normalized part number to better search keys (CAD file
    name, then CD- number), tried before the part number itself.
    """
    unique: list[str] = []
    seen: set[str] = set()
    for raw in numbers:
        key = normalize_part_number(raw)
        if key and key not in seen:
            seen.add(key)
            unique.append(str(raw).strip())
    if not unique:
        return {}, []

    cfg = vault_config(config_path)
    if not cfg:
        return {}, ["No Vault connection configured. The State column is left blank."]

    try:
        return _run(lambda: _fetch_states(cfg, unique, aliases)), []
    except Exception as exc:      # noqa: BLE001 — Vault must never break the sheet
        return {}, [f"Could not read Vault states ({exc}); State is left blank."]
