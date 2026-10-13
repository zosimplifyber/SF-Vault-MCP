"""Client for the SF Vault Bridge Inventor add-in (inventor-addin/).

The add-in runs inside Inventor and does Vault writes on the user's own
signed-in session, so no second Vault licence seat is needed. Function names
and returns match the scripts/vault_sdk.py wrappers the McMaster intake used
before. Spec: docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md
"""
from __future__ import annotations

import http.client
import json
import os
import socket
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Iterable

BASE_URL = "http://localhost:8790"
TOKEN_HEADER = "X-Bridge-Token"
DEFAULT_TIMEOUT_SECONDS = 300
NOT_RUNNING = ("Inventor is not running, or the SF Vault Bridge add-in is not loaded. "
               "Start Inventor and sign in to Vault.")


class BridgeUnavailable(RuntimeError):
    """Inventor, the add-in or the Vault sign-in is missing. Nothing was done."""


class BridgeError(RuntimeError):
    """The bridge answered with an error code."""

    def __init__(self, code: str, message: str):
        super().__init__(f"{code}: {message}")
        self.code = code


def token_path() -> Path:
    return Path(os.environ.get("APPDATA", "")) / "Simplifyber" / "VaultBridge" / "token"


def _call(op: str, body: dict[str, Any] | None = None, *, timeout: float = DEFAULT_TIMEOUT_SECONDS) -> dict[str, Any]:
    try:
        token = token_path().read_text(encoding="utf-8").strip()
    except OSError:
        raise BridgeUnavailable("The SF Vault Bridge has never started on this PC. Install it and start Inventor.") from None
    req = urllib.request.Request(
        f"{BASE_URL}/op/{op}", data=json.dumps(body or {}).encode("utf-8"), method="POST",
        headers={"Content-Type": "application/json", TOKEN_HEADER: token})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as exc:
        try:
            error_body = exc.read()
        except (TimeoutError, socket.timeout):
            raise BridgeError("timeout", f"The bridge did not answer {op} within {timeout:g} s. It may still have finished; check Vault.") from None
        except (ConnectionError, http.client.HTTPException):
            raise BridgeError("connection_lost", f"The bridge connection dropped during {op}. It may still have finished; check Vault.") from None
        try:
            payload = json.loads(error_body or b"{}")
        except ValueError:
            payload = {}
        code = payload.get("error") or f"http_{exc.code}"
        message = payload.get("message") or str(exc)
        if code == "not_signed_in":
            raise BridgeUnavailable(message) from None
        raise BridgeError(code, message) from None
    except (TimeoutError, socket.timeout):
        raise BridgeError("timeout", f"The bridge did not answer {op} within {timeout:g} s. It may still have finished; check Vault.") from None
    except urllib.error.URLError as exc:
        # Raised before any response: nothing was done.
        if isinstance(exc.reason, (TimeoutError, socket.timeout)):
            raise BridgeError("timeout", f"The bridge did not answer {op} within {timeout:g} s. It may still have finished; check Vault.") from None
        raise BridgeUnavailable(NOT_RUNNING) from exc
    except ConnectionRefusedError as exc:
        raise BridgeUnavailable(NOT_RUNNING) from exc
    except (ConnectionError, http.client.HTTPException):
        # Dropped after the request was sent: the write may have completed.
        raise BridgeError("connection_lost", f"The bridge connection dropped during {op}. It may still have finished; check Vault.") from None


def status() -> dict[str, Any]:
    return _call("status", timeout=10)


def check_inventor_running() -> dict[str, Any]:
    """Status, or BridgeUnavailable when Inventor's Vault add-in is not signed in."""
    s = status()
    if not s.get("signed_in"):
        raise BridgeUnavailable("Sign in to Vault in Inventor (Vault tab, Log In).")
    return s


def get_numbering_schemes() -> dict[str, Any]:
    return _call("get_numbering_schemes")


def generate_file_number(scheme_id: int | str) -> dict[str, Any]:
    return _call("generate_file_number", {"schemeId": int(scheme_id)})


def get_folder_by_path(path: str) -> dict[str, Any]:
    return _call("get_folder_by_path", {"path": path})


def add_folder(parent_path: str, name: str) -> dict[str, Any]:
    return _call("add_folder", {"parentPath": parent_path, "name": name})


def get_file_categories() -> dict[str, Any]:
    return _call("get_file_categories")


def add_file(folder_path: str, local_path: str, file_name: str, *,
             attachment_file_ids: Iterable[int | str] = (), comment: str = "") -> dict[str, Any]:
    return _call("add_file", {
        "folderPath": folder_path, "localPath": str(local_path), "fileName": file_name,
        "attachmentFileIds": [int(i) for i in attachment_file_ids], "comment": comment,
    })


def update_file_categories(master_ids: Iterable[int | str], category_id: int | str, *, comment: str = "") -> dict[str, Any]:
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call("update_file_categories", {"masterIds": ids, "categoryId": int(category_id), "comment": comment})


def import_step_as_part(step_path: str | Path, out_ipt: str | Path, iproperties: dict) -> Path:
    out = _call("import_step_as_part", {"stepPath": str(step_path), "outIpt": str(out_ipt), "iproperties": iproperties})
    return Path(out.get("outIpt") or out_ipt)


def fix_data_card(files: Iterable[str], design: dict[str, str] | None = None, custom: dict[str, str] | None = None,
                  part_number_from_file_name: bool = True, dry_run: bool = True, comment: str | None = None,
                  revise_released: bool = False) -> dict[str, Any]:
    """Fix iProperties of existing Vault parts (check out, edit in Inventor, check in). Dry run by default.

    revise_released=True also fixes Released files: Released -> Work in Progress, edit, check in, back to Released."""
    names = [str(f) for f in files]
    if not names:
        raise ValueError("files must not be empty")
    body: dict[str, Any] = {"files": names, "partNumberFromFileName": bool(part_number_from_file_name), "dryRun": bool(dry_run)}
    if design:
        body["design"] = dict(design)
    if custom:
        body["custom"] = dict(custom)
    if comment:
        body["comment"] = comment
    if revise_released:
        body["reviseReleased"] = True
    return _call("fix_data_card", body)


def _pairs(items: Iterable[str], flag: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for item in items:
        name, sep, value = item.partition("=")
        if not sep or not name.strip():
            raise SystemExit(f"{flag} expects NAME=VALUE, got {item!r}")
        out[name.strip()] = value
    return out


def build_parser():
    import argparse
    parser = argparse.ArgumentParser(description="SF Vault Bridge client")
    sub = parser.add_subparsers(dest="command", required=True)
    fix = sub.add_parser("fix-data-card", help="Fix iProperties of existing Vault parts (dry run unless --apply)")
    fix.add_argument("files", nargs="+", help="file names (CD-001885.ipt) or full $/ paths")
    fix.add_argument("--set", action="append", default=[], metavar="NAME=VALUE", help="Design Tracking iProperty to set (repeatable)")
    fix.add_argument("--custom", action="append", default=[], metavar="NAME=VALUE", help="custom iProperty to set or add (repeatable)")
    fix.add_argument("--no-part-number", action="store_true", help="do not set Part Number to the file name")
    fix.add_argument("--apply", action="store_true", help="check out, fix and check in (default is a preview)")
    fix.add_argument("--comment", default=None, help="check-in comment")
    fix.add_argument("--revise-released", action="store_true",
                     help="also fix Released files: Released -> Work in Progress, edit, check in, back to Released (never Content Center)")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "fix-data-card":
        result = fix_data_card(args.files, design=_pairs(args.set, "--set"), custom=_pairs(args.custom, "--custom"),
                               part_number_from_file_name=not args.no_part_number, dry_run=not args.apply, comment=args.comment,
                               revise_released=args.revise_released)
        print(json.dumps(result, indent=2))
        counts = result.get("counts", {})
        return 1 if counts.get("error") else 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
