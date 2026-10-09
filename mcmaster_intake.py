"""McMaster part intake: file a hand-downloaded McMaster STEP + PDF into Vault
as a numbered, categorized Inventor part with a spec card attached, and serve
spec cards back for parts and assemblies.

Spec: docs/superpowers/specs/2026-10-09-mcmaster-intake-design.md
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import re
import secrets
import shutil
import time
from datetime import date
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


PART_CATEGORY = "Part - Purchased"
DOC_CATEGORY = "Documents"
WIP_STATE = "Work in Progress"
RULES_PATH = Path(__file__).resolve().parent / "file_property_rules.json"


async def find_by_vendor_number(api, vault: str, pn: str) -> Optional[dict[str, Any]]:
    import vault_slim as slim
    defs = await api.get_property_definitions(vault_id=vault, limit=200)
    if defs.get("error"):
        raise IntakeError(f"Could not read Vault property definitions: {defs.get('data')}")
    prop_id = next((d.get("id") for d in slim._records(defs.get("data"))
                    if "vendor number" in (str(d.get("displayName", "")).strip().lower(),
                                           str(d.get("systemName", "")).strip().lower())), None)
    if prop_id is None:
        raise IntakeError("Vault has no 'Vendor Number' property")
    body = {"searchCriterias": [{
        "propertyDefinitionUrl": f"/AutodeskDM/Services/api/vault/v2/vaults/{vault}/property-definitions/{prop_id}",
        "operator": "IsExactly", "searchString": pn}],
        "entityTypesToSearch": ["File"]}
    res = await api.advanced_search(vault_id=vault, body=body, limit=5)
    if res.get("error"):
        raise IntakeError(f"Vendor Number search failed: {res.get('data')}")
    hits = slim._records(res.get("data"))
    if not hits:
        return None
    h = hits[0]
    return {"name": h.get("name"), "file_id": (h.get("file") or {}).get("id") or h.get("id"),
            "folder": (h.get("parentFolder") or {}).get("fullName")}


def _intake_config(config: Optional[dict[str, Any]]) -> dict[str, Any]:
    if config is None:
        cfg_path = Path(__file__).resolve().parent / "config.json"
        try:
            config = json.loads(cfg_path.read_text(encoding="utf-8-sig")).get("intake", {})
        except (OSError, ValueError):
            config = {}
    return {
        "downloads_dir": Path(config.get("downloads_dir") or Path.home() / "Downloads"),
        "workspace_root": Path(config.get("workspace_root") or r"C:\Vault Workspace"),
        "numbering_scheme": config.get("numbering_scheme") or "",
    }


def register(mcp, api, resolved_vault, *, config: Optional[dict[str, Any]] = None,
             store: Optional[PreviewStore] = None, inventor=None, sleep=asyncio.sleep) -> None:
    from vault_write_tools import _dump, _sdk, _sdk_error
    from part_specs import validate_card

    cfg = _intake_config(config)
    store = store or PreviewStore()

    def _inventor():
        if inventor is not None:
            return inventor
        import inventor_automation
        return inventor_automation

    def err(message: str, **extra: Any) -> str:
        return _dump({"error": True, "message": message, **extra})

    @mcp.tool()
    async def vault_mcmaster_intake_preview(mcmaster_pn: str, spec_card_json: str, description: str,
                                            finish: str = "", folder: str = "",
                                            vault_id_param: str = "") -> str:
        """Preview adding a McMaster part to Vault. Writes nothing.

        Zak downloads the part's STEP and PDF from McMaster into Downloads
        first. Read the PDF and fill the spec card (schema in
        docs/superpowers/specs/2026-10-09-mcmaster-intake-design.md).
        Show Zak the whole result; on his yes call
        vault_mcmaster_intake_commit(token).

        Args:
            mcmaster_pn: McMaster part number, e.g. 90510A313.
            spec_card_json: Spec card JSON; cd_number may be null.
            description: McMaster's product name, e.g. "Fully Threaded T-Slot Nut".
            finish: Material Finish iProperty, e.g. "Black Oxide".
            folder: McMaster-Carr sub-folder name; default comes from the card type.
            vault_id_param: Vault ID. Leave empty for the configured vault.
        """
        vault = resolved_vault(vault_id_param)
        try:
            pn = normalize_pn(mcmaster_pn)
            existing = await find_by_vendor_number(api, vault, pn)
            if existing:
                return _dump({"duplicate": True, "existing": existing,
                              "message": f"McMaster {pn} is already in Vault as {existing['name']}. Use that part."})
            try:
                card = json.loads(spec_card_json)
            except ValueError as exc:
                return err(f"spec_card_json is not valid JSON: {exc}")
            problems = validate_card(card)
            if problems:
                return err("The spec card has problems.", problems=problems)
            card_pn = normalize_pn(str(card["source"]["vendor_number"]))
            if card_pn != pn:
                return err(f"The spec card is for {card_pn}, not {pn}.")
            files = find_downloads(pn, cfg["downloads_dir"])
            vault_folder = target_folder(card["type"], folder)
        except IntakeError as exc:
            return err(str(exc))
        try:
            folder_info = await asyncio.to_thread(_sdk().get_folder_by_path, vault_folder)
        except Exception as exc:
            return _dump(_sdk_error(exc))
        payload = {
            "vault": vault, "pn": pn, "card": card, "description": description, "finish": finish,
            "folder": vault_folder, "folder_exists": bool(folder_info.get("found")),
            "files": {k: str(v) for k, v in files.items()},
            "hashes": {k: sha256_file(v) for k, v in files.items()},
        }
        token = store.put(payload)
        names = file_names("CD-<next>", pn)
        return _dump({
            "preview": True, "token": token, "expires_in_minutes": 15,
            "cd_number": "next available, assigned at commit",
            "folder": vault_folder, "new_folder": not payload["folder_exists"],
            "files": payload["files"],
            "will_create": [names["ipt"], names["pdf"], names["specs"]],
            "category": PART_CATEGORY, "attachments_category": DOC_CATEGORY, "state": WIP_STATE,
            "iproperties": build_iproperties("CD-<next>", pn, description, finish),
            "spec_card": card,
            "next": "Show this to Zak. Only on his yes call vault_mcmaster_intake_commit with this token.",
        })

    @mcp.tool()
    async def vault_mcmaster_intake_commit(token: str, vault_id_param: str = "") -> str:
        """Add the previewed McMaster part to Vault. Only after Zak said yes
        to the preview. Close Vault Explorer first (SDK license seat).

        Args:
            token: The token from vault_mcmaster_intake_preview.
            vault_id_param: Ignored; the preview's vault is used.
        """
        p = store.take(token)
        if p is None:
            return err("Preview token is unknown, already used, or expired. Run the preview again.")
        steps: list[dict[str, Any]] = []
        written: list[dict[str, Any]] = []
        sdk = _sdk()

        def failed(step: str, exc: Exception) -> str:
            out = {"applied": False, "failed_step": step, "error": str(exc), "steps": steps,
                   "already_in_vault": written}
            if written:
                out["remediation"] = "Delete or fix the files listed in already_in_vault in Vault Explorer."
            sdk_err = _sdk_error(exc)
            if "remediation" in sdk_err:
                out["license"] = sdk_err["remediation"]
            return _dump(out)

        async def run(fn, *a, **kw):
            return await asyncio.to_thread(fn, *a, **kw)

        # recheck: downloads unchanged and still not a duplicate
        try:
            for key, path in p["files"].items():
                if not Path(path).is_file() or sha256_file(Path(path)) != p["hashes"][key]:
                    raise IntakeError(f"{Path(path).name} changed or disappeared since the preview. Run the preview again.")
            dup = await find_by_vendor_number(api, p["vault"], p["pn"])
            if dup:
                raise IntakeError(f"McMaster {p['pn']} was added as {dup['name']} since the preview.")
        except Exception as exc:
            return failed("recheck", exc)
        steps.append({"step": "recheck", "ok": True})

        try:
            schemes = (await run(sdk.get_numbering_schemes)).get("schemes") or []
            want = cfg["numbering_scheme"]
            scheme = next((s for s in schemes if s["name"] == want), None) if want else \
                next((s for s in schemes if s.get("isDefault")), None)
            if scheme is None:
                raise IntakeError(f"Numbering scheme {want or '(default)'} not found in Vault")
            cd = str((await run(sdk.generate_file_number, scheme["id"])).get("number") or "")
            if not re.match(r"^CD-\d{6}$", cd):
                raise IntakeError(f"Vault generated {cd!r}, which is not a CD-###### number")
        except Exception as exc:
            return failed("number", exc)
        steps.append({"step": "number", "ok": True, "cd_number": cd})

        folder_path = p["folder"]
        if not p["folder_exists"]:
            try:
                parent, name = folder_path.rsplit("/", 1)
                await run(sdk.add_folder, parent, name)
                written.append({"name": folder_path, "kind": "folder"})
            except Exception as exc:
                return failed("folder", exc)
            steps.append({"step": "folder", "ok": True})

        names = file_names(cd, p["pn"])
        local = local_dir_for(folder_path, cfg["workspace_root"])
        ipt = local / names["ipt"]
        try:
            await run(_inventor().import_step_as_part, p["files"]["step"], ipt,
                      build_iproperties(cd, p["pn"], p["description"], p["finish"]))
        except Exception as exc:
            return failed("inventor", exc)
        steps.append({"step": "inventor", "ok": True, "local_file": str(ipt)})

        try:
            pdf_local = local / names["pdf"]
            shutil.copy2(p["files"]["pdf"], pdf_local)
            card = dict(p["card"], cd_number=cd)
            card["source"] = dict(card["source"], pdf=names["pdf"], read_on=card["source"].get("read_on") or date.today().isoformat())
            specs_local = local / names["specs"]
            specs_local.write_text(json.dumps(card, indent=2), encoding="utf-8")
            pdf = await run(sdk.add_file, folder_path, str(pdf_local), names["pdf"], comment=f"McMaster {p['pn']} drawing")
            written.append(pdf)
            specs = await run(sdk.add_file, folder_path, str(specs_local), names["specs"], comment=f"McMaster {p['pn']} spec card")
            written.append(specs)
        except Exception as exc:
            return failed("documents", exc)
        steps.append({"step": "documents", "ok": True})

        try:
            part = await run(sdk.add_file, folder_path, str(ipt), names["ipt"],
                             attachment_file_ids=[pdf["id"], specs["id"]], comment=f"McMaster {p['pn']} intake")
            written.append(part)
        except Exception as exc:
            return failed("part", exc)
        steps.append({"step": "part", "ok": True})

        try:
            cats = {c["name"]: c["id"] for c in (await run(sdk.get_file_categories)).get("categories") or []}
            for needed in (PART_CATEGORY, DOC_CATEGORY):
                if needed not in cats:
                    raise IntakeError(f"Vault has no file category {needed!r}")
            await run(sdk.update_file_categories, [part["masterId"]], cats[PART_CATEGORY], comment="McMaster intake")
            await run(sdk.update_file_categories, [pdf["masterId"], specs["masterId"]], cats[DOC_CATEGORY], comment="McMaster intake")
        except Exception as exc:
            return failed("category", exc)
        steps.append({"step": "category", "ok": True})

        problems = await _verify(api, p["vault"], names["ipt"], sleep)
        steps.append({"step": "verify", "ok": not problems})

        done = Path(cfg["downloads_dir"]) / DONE_FOLDER
        done.mkdir(exist_ok=True)
        for path in p["files"].values():
            shutil.move(path, done / Path(path).name)
        steps.append({"step": "tidy", "ok": True})

        return _dump({"applied": True, "verified": not problems, "cd_number": cd,
                      "vault_path": f"{folder_path}/{names['ipt']}", "problems": problems, "steps": steps})


async def _verify(api, vault: str, ipt_name: str, sleep, attempts: int = 3) -> list[str]:
    """Read the new part back; return human-readable problems (empty = OK).
    Retries because Vault indexes properties a moment after check-in."""
    import check_file_properties as cfp
    from check_item_properties import load_json
    rules = load_json(RULES_PATH)
    problems: list[str] = []
    for attempt in range(attempts):
        rec = await cfp.fetch_file(api, vault, ipt_name)
        props = rec.get("properties") or {}
        problems = []
        if not props:
            problems.append(f"{ipt_name} not found in Vault yet ({rec.get('note') or 'no properties'})")
        else:
            if props.get("Category Name") != PART_CATEGORY:
                problems.append(f"Category is {props.get('Category Name')!r}, expected {PART_CATEGORY!r}")
            if props.get("State") != WIP_STATE:
                problems.append(f"State is {props.get('State')!r}, expected {WIP_STATE!r}")
            result = cfp.evaluate_against_rules(props, props.get("Category Name") or "", rules)
            for r in (result.get("report") or {}).get("results", []):
                if not r["passed"]:
                    problems.append(f"{r['property']}: " + "; ".join(r["failures"]))
        if not problems:
            return []
        if attempt < attempts - 1:
            await sleep(3)
    return problems
