"""Live probe for the McMaster intake SDK operations. Run by hand, once.

Read-only unless --write. With --write it creates
$/DESIGNS/PLAYGROUND/ZOlech/intake-probe if missing, adds probe.json, adds
probe.pdf with probe.json attached, and sets both to Documents. Delete the
folder in Vault Explorer afterwards.

    python scripts/probes/probe_intake_sdk.py
    python scripts/probes/probe_intake_sdk.py --write
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
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
    a = ap.parse_args()
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


async def _print_uses(file_version_id: int) -> None:
    from app import CONFIG_FILE, authenticate, load_config
    from vault_rest_api import VaultRestAPI
    cfg = load_config(CONFIG_FILE)["vault"]
    api = VaultRestAPI(servername=cfg["servername"])
    vault_id = await authenticate(api, cfg)
    uses = await api.get_file_uses(vault_id=vault_id, file_version_id=str(file_version_id))
    print("REST uses of probe.pdf (look for the attachment fileAssocType):")
    print(json.dumps(uses.get("data"), indent=2, default=str)[:4000])


if __name__ == "__main__":
    main()
