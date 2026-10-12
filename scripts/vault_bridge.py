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
