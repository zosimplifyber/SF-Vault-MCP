import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import vault_sdk  # noqa: E402


@pytest.fixture
def calls(monkeypatch):
    seen = []

    def fake_call_ps(operation, args=None, *, timeout=vault_sdk.DEFAULT_TIMEOUT_SECONDS):
        seen.append((operation, args or {}))
        return {"ok": True}

    monkeypatch.setattr(vault_sdk, "_call_ps", fake_call_ps)
    return seen


def test_read_ops_send_expected_operations(calls):
    vault_sdk.get_file_categories()
    vault_sdk.get_numbering_schemes()
    vault_sdk.get_folder_by_path("$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts")
    assert [c[0] for c in calls] == ["GetFileCategories", "GetNumberingSchemes", "GetFolderByPath"]
    assert calls[2][1] == {"path": "$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts"}


def test_generate_file_number_passes_int_scheme(calls):
    vault_sdk.generate_file_number("7")
    assert calls == [("GenerateFileNumber", {"schemeId": 7})]


def test_add_folder_args(calls):
    vault_sdk.add_folder("$/DESIGNS/Simplifyber Library/McMaster-Carr", "t-slot")
    assert calls == [("AddFolder", {"parentPath": "$/DESIGNS/Simplifyber Library/McMaster-Carr", "name": "t-slot"})]


def test_add_file_args_with_attachments(calls, tmp_path):
    f = tmp_path / "CD-000001.ipt"
    f.write_bytes(b"x")
    vault_sdk.add_file("$/X", str(f), "CD-000001.ipt", attachment_file_ids=["11", 12], comment="intake")
    op, args = calls[0]
    assert op == "AddFile"
    assert args == {"folderPath": "$/X", "localPath": str(f), "fileName": "CD-000001.ipt",
                    "attachmentFileIds": [11, 12], "comment": "intake"}


def test_add_file_rejects_missing_local_file(calls, tmp_path):
    with pytest.raises(FileNotFoundError):
        vault_sdk.add_file("$/X", str(tmp_path / "nope.pdf"), "nope.pdf")
    assert calls == []


def test_update_file_categories_args(calls):
    vault_sdk.update_file_categories(["5", 6], "3", comment="intake")
    assert calls == [("UpdateFileCategories", {"masterIds": [5, 6], "categoryId": 3, "comment": "intake"})]


def test_update_file_categories_rejects_empty(calls):
    with pytest.raises(ValueError):
        vault_sdk.update_file_categories([], 3)
