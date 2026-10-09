"""Live probe for the McMaster intake SDK operations. Run by hand, once.

Read-only unless --write. With --write it creates
$/DESIGNS/PLAYGROUND/ZOlech/intake-probe if missing, adds probe.json, adds
probe.pdf with probe.json attached, and sets both to Documents. Delete the
folder in Vault Explorer afterwards.

    python scripts/probes/probe_intake_sdk.py
    python scripts/probes/probe_intake_sdk.py --write
    python scripts/probes/probe_intake_sdk.py --write --ipt C:\path\probe-part.ipt

--ipt adds an existing .ipt to the probe folder, sets it to Part - Purchased,
waits 10 s, then runs the Vendor Number search the intake uses and prints
whether it found the file, plus its State. Save a scratch part from Inventor
first, with custom iProperty "Vendor Number" = "PROBE-0000".
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / "scripts")]

import vault_sdk  # noqa: E402

PROBE_PARENT = "$/DESIGNS/PLAYGROUND/ZOlech"
PROBE_FOLDER = PROBE_PARENT + "/intake-probe"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--ipt", help="existing .ipt with custom iProperty Vendor Number = PROBE-0000 (needs --write)")
    a = ap.parse_args()
    if a.ipt and not a.write:
        ap.error("--ipt requires --write")
    if a.ipt and not Path(a.ipt).is_file():
        ap.error(f"--ipt file not found: {a.ipt}")
    print("Numbering schemes:", json.dumps(vault_sdk.get_numbering_schemes(), indent=2))
    print("File categories:", json.dumps(vault_sdk.get_file_categories(), indent=2))
    print("Library root:", vault_sdk.get_folder_by_path("$/DESIGNS/Simplifyber Library/McMaster-Carr"))
    if not a.write:
        return
    if not vault_sdk.get_folder_by_path(PROBE_FOLDER).get("found"):
        print("AddFolder:", vault_sdk.add_folder(PROBE_PARENT, "intake-probe"))
    tmp = Path(tempfile.mkdtemp())
    (tmp / "probe.json").write_text('{"probe": true}', encoding="utf-8")
    (tmp / "probe.pdf").write_bytes(b"%PDF-1.4\n%probe\n")
    j = vault_sdk.add_file(PROBE_FOLDER, str(tmp / "probe.json"), "probe.json", comment="intake probe")
    print("AddFile json:", j)
    p = vault_sdk.add_file(PROBE_FOLDER, str(tmp / "probe.pdf"), "probe.pdf",
                           attachment_file_ids=[j["id"]], comment="intake probe")
    print("AddFile pdf (json attached):", p)
    cats = {c["name"]: c["id"] for c in vault_sdk.get_file_categories()["categories"]}
    print("UpdateFileCategories:", vault_sdk.update_file_categories(
        [j["masterId"], p["masterId"]], cats["Documents"], comment="intake probe"))
    asyncio.run(_print_uses(p["id"]))
    if a.ipt:
        ipt_name = Path(a.ipt).name
        i = vault_sdk.add_file(PROBE_FOLDER, a.ipt, ipt_name, comment="intake probe")
        print("AddFile ipt:", i)
        print("UpdateFileCategories (ipt):", vault_sdk.update_file_categories(
            [i["masterId"]], cats["Part - Purchased"], comment="intake probe"))
        print("Waiting 10 s for Vault to index the properties...")
        time.sleep(10)
        asyncio.run(_probe_vendor_search(ipt_name))


async def _print_uses(file_version_id: int) -> None:
    from app import CONFIG_FILE, authenticate, load_config
    from vault_rest_api import VaultRestAPI
    cfg = load_config(CONFIG_FILE)["vault"]
    api = VaultRestAPI(servername=cfg["servername"])
    vault_id = await authenticate(api, cfg)
    uses = await api.get_file_uses(vault_id=vault_id, file_version_id=str(file_version_id))
    print("REST uses of probe.pdf (look for the attachment fileAssocType):")
    print(json.dumps(uses.get("data"), indent=2, default=str)[:4000])


async def _probe_vendor_search(ipt_name: str) -> None:
    import check_file_properties as cfp
    import mcmaster_intake
    from app import CONFIG_FILE, authenticate, load_config
    from vault_rest_api import VaultRestAPI
    cfg = load_config(CONFIG_FILE)["vault"]
    api = VaultRestAPI(servername=cfg["servername"])
    vault_id = await authenticate(api, cfg)
    hit = await mcmaster_intake.find_by_vendor_number(api, vault_id, "PROBE-0000")
    print("Vendor Number search for PROBE-0000:", "FOUND" if hit else "NOT FOUND", hit)
    rec = await cfp.fetch_file(api, vault_id, ipt_name)
    props = rec.get("properties") or {}
    print("State:", props.get("State"), "| Category:", props.get("Category Name"), "| note:", rec.get("note"))


if __name__ == "__main__":
    main()
