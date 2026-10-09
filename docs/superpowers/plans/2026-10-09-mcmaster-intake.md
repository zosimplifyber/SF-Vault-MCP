# McMaster Part Intake and Part Specs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three MCP tools that file a hand-downloaded McMaster STEP + PDF into Vault as a numbered, categorized, rule-checked Inventor part with a spec card attached, and return spec cards for a part or a whole assembly.

**Architecture:** Pure helpers (`part_specs.py`, `mcmaster_intake.py` top half) do validation, matching and lookups with no I/O beyond the filesystem. Vault writes go through new operations on the existing PowerShell SDK bridge (`scripts/vault_sdk.ps1` + `scripts/vault_sdk.py`); Inventor work goes through a new COM function in `scripts/inventor_automation.py`. `mcmaster_intake.register()` wires the tools into FastMCP the same way `vault_write_tools.register()` does.

**Tech Stack:** Python 3.14, FastMCP, pytest (+ pytest-asyncio, `asyncio_mode = auto`), PowerShell 5.1 + Vault 2026 SDK (WebServiceManager and VDF), Inventor COM via pywin32.

**Spec:** `docs/superpowers/specs/2026-10-09-mcmaster-intake-design.md`

## Global Constraints

- Nothing writes to Vault without a preview and Zak's yes. The commit tool only accepts a token from a preview.
- McMaster part number already in Vault (`Vendor Number` match): stop and return the existing file. Never create a duplicate.
- New .ipt category: **Part - Purchased**. PDF and spec card category: **Documents**. State stays **Work in Progress**.
- New .ipt must pass `file_property_rules.json` for its category; each failure is reported by property name.
- Library root: `$/DESIGNS/Simplifyber Library/McMaster-Carr`. File names: `<CD>.ipt`, `<CD> McMaster <pn>.pdf`, `<CD>.specs.json`. CD numbers match `^CD-\d{6}$`.
- Downloads live in `%USERPROFILE%\Downloads`; processed files move to `Downloads\McMaster intake done\`.
- No McMaster API calls, no scraping, no automatic downloads.
- On any failed commit step: stop, name the step, list what already reached Vault. No automatic rollback.
- Unknown spec values are `null`, never omitted. Dimensions are numbers in mm.
- No live Vault or Inventor in unit tests. Tests fake `vault_sdk` via `monkeypatch.setitem(sys.modules, "vault_sdk", fake)`.
- User-facing strings: no em dashes.

## Review Focus

1. **Same part downloaded twice** (`90510A313_...STEP` and `90510A313_... (1).STEP`): the newest file by modified time wins, and the preview names the file it picked. Test in Task 4.
2. **Spec card values given as text** (`"slot_width": "16 mm"`): the preview rejects the card and names the field. Test in Task 3.
3. **Download changed or deleted between preview and commit:** commit refuses before any Vault write and asks for a new preview. Test in Task 6.
4. **Commit called twice with one token:** the second call is refused, no second part is created. Test in Task 6.
5. **McMaster number typed with lowercase or spaces** (`" 90510a313"`): normalized before the duplicate check and file matching, so the duplicate is still caught. Test in Task 4 (normalize) and Task 6 (duplicate).

---

## File Structure

| File | Responsibility |
|---|---|
| `scripts/vault_sdk.ps1` (modify) | New operations: GetFileCategories, GetNumberingSchemes, GetFolderByPath (read-only); GenerateFileNumber, AddFolder, AddFile, UpdateFileCategories (write). Keep the VDF Connection for AddFile. |
| `scripts/vault_sdk.py` (modify) | Python wrappers for the seven operations. |
| `scripts/probes/probe_intake_sdk.py` (create) | Live probe Zak runs once: lists schemes and categories, adds a test PDF + attachment in a scratch folder. |
| `part_specs.py` (create) | Spec card schema and validation, Content Center filename parsing, standard-table cards. |
| `standard_specs.json` (create) | ISO/DIN dimension tables. |
| `mcmaster_intake.py` (create) | Pure intake helpers, preview token store, `register()` with the three MCP tools. |
| `scripts/inventor_automation.py` (modify) | `set_iproperties`, `import_step_as_part`. |
| `mcp_server.py` (modify) | Register `mcmaster_intake`. |
| `config.json.example` (modify) | `intake` block. |
| `tests/test_vault_sdk_intake_ops.py`, `tests/test_part_specs.py`, `tests/test_mcmaster_intake_helpers.py`, `tests/test_inventor_step_import.py`, `tests/test_mcmaster_intake_tools.py`, `tests/test_part_specs_tool.py` (create) | Tests. |

Branch: work on `feat/mcmaster-intake` created from `spec/mcmaster-intake`.

---

### Task 1: SDK bridge operations

**Files:**
- Modify: `scripts/vault_sdk.ps1` (header comment lines 8-21, `Connect-Vault` at :164, new `Invoke-*` functions before the dispatch block, `$readOnlyOps` and `switch` at :731-760)
- Modify: `scripts/vault_sdk.py` (new wrappers, `__all__` at :413)
- Test: `tests/test_vault_sdk_intake_ops.py`

**Interfaces:**
- Consumes: `_call_ps(operation, args, *, timeout)` in `scripts/vault_sdk.py:88`.
- Produces (all in `vault_sdk`):
  - `get_file_categories() -> dict` → `{"categories": [{"id": int, "name": str}]}`
  - `get_numbering_schemes() -> dict` → `{"schemes": [{"id": int, "name": str, "isDefault": bool}]}`
  - `get_folder_by_path(path: str) -> dict` → `{"found": bool, "id": int, "fullName": str, "isLibrary": bool}` (only `found` when missing)
  - `generate_file_number(scheme_id: int) -> dict` → `{"number": str}`
  - `add_folder(parent_path: str, name: str) -> dict` → `{"id": int, "fullName": str}`
  - `add_file(folder_path: str, local_path: str, file_name: str, *, attachment_file_ids: list[int] = (), comment: str = "") -> dict` → `Get-FileOut` shape `{"id", "masterId", "name", "checkedOut", "ckOutUserId"}`
  - `update_file_categories(master_ids: Iterable[int], category_id: int, *, comment: str = "") -> dict` → `{"updated": [Get-FileOut, ...]}`

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_vault_sdk_intake_ops.py
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_vault_sdk_intake_ops.py -v`
Expected: FAIL with `AttributeError: module 'vault_sdk' has no attribute 'get_file_categories'`

- [ ] **Step 3: Add the Python wrappers** (after `update_file_properties` in `scripts/vault_sdk.py`)

```python
def get_file_categories() -> dict[str, Any]:
    """All FILE categories: {"categories": [{"id", "name"}]}."""
    return _call_ps("GetFileCategories")


def get_numbering_schemes() -> dict[str, Any]:
    """Activated FILE numbering schemes: {"schemes": [{"id", "name", "isDefault"}]}."""
    return _call_ps("GetNumberingSchemes")


def get_folder_by_path(path: str) -> dict[str, Any]:
    """{"found": False} when the folder does not exist, else id/fullName/isLibrary."""
    return _call_ps("GetFolderByPath", {"path": path})


def generate_file_number(scheme_id: int | str) -> dict[str, Any]:
    """Consume the next number from a FILE numbering scheme: {"number": str}."""
    return _call_ps("GenerateFileNumber", {"schemeId": int(scheme_id)})


def add_folder(parent_path: str, name: str) -> dict[str, Any]:
    """Create a sub-folder; it inherits the parent's library flag."""
    return _call_ps("AddFolder", {"parentPath": parent_path, "name": name})


def add_file(folder_path: str, local_path: str, file_name: str, *,
             attachment_file_ids: Iterable[int | str] = (), comment: str = "") -> dict[str, Any]:
    """Add a new file to Vault (checked in), optionally attaching existing
    files by file-version id. Returns the new file's id/masterId/name."""
    if not Path(local_path).is_file():
        raise FileNotFoundError(local_path)
    return _call_ps("AddFile", {
        "folderPath": folder_path, "localPath": str(local_path), "fileName": file_name,
        "attachmentFileIds": [int(i) for i in attachment_file_ids], "comment": comment,
    }, timeout=300)


def update_file_categories(master_ids: Iterable[int | str], category_id: int | str, *,
                           comment: str = "") -> dict[str, Any]:
    """Set the category on the latest version of each file (by master id)."""
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call_ps("UpdateFileCategories",
                    {"masterIds": ids, "categoryId": int(category_id), "comment": comment})
```

Add `Path` to the imports if missing (`from pathlib import Path`) and append the seven names to `__all__`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pytest tests/test_vault_sdk_intake_ops.py -v`
Expected: 7 passed

- [ ] **Step 5: Keep the VDF connection in `Connect-Vault`**

In `scripts/vault_sdk.ps1`, at every `return $r.Connection.WebServiceManager` inside `Connect-Vault` (the read-only branch and the writable loop), add the line before it:

```powershell
        $script:VdfConn = $r.Connection
```

- [ ] **Step 6: Add the PowerShell operations** (before the `$readOnlyOps` line)

```powershell
function Invoke-GetFileCategories($mgr) {
    $cats = $mgr.CategoryService.GetCategoriesByEntityClassId('FILE', $true)
    return @{ categories = @($cats | ForEach-Object { [ordered]@{ id = [int64]$_.Id; name = [string]$_.Name } }) }
}

function Invoke-GetNumberingSchemes($mgr) {
    $s = $mgr.NumberingService.GetNumberingSchemes('FILE', [Autodesk.Connectivity.WebServices.NumSchmType]::Activated)
    return @{ schemes = @($s | ForEach-Object { [ordered]@{ id = [int64]$_.SchmID; name = [string]$_.Name; isDefault = [bool]$_.IsDflt } }) }
}

function Invoke-GetFolderByPath($mgr) {
    $p = [string](Get-Arg 'path')
    try { $f = $mgr.DocumentService.GetFolderByPath($p) } catch { return @{ found = $false } }
    if ($null -eq $f) { return @{ found = $false } }
    return [ordered]@{ found = $true; id = [int64]$f.Id; fullName = [string]$f.FullName; isLibrary = [bool]$f.IsLibrary }
}

function Invoke-GenerateFileNumber($mgr) {
    $id = Get-Arg 'schemeId'
    if ($null -eq $id) { Die 'GenerateFileNumber requires schemeId' }
    $n = $mgr.DocumentService.GenerateFileNumber([int64]$id, [string[]]@())
    return @{ number = [string]$n }
}

function Invoke-AddFolder($mgr) {
    $parent = $mgr.DocumentService.GetFolderByPath([string](Get-Arg 'parentPath'))
    $f = $mgr.DocumentService.AddFolder([string](Get-Arg 'name'), [int64]$parent.Id, [bool]$parent.IsLibrary)
    return [ordered]@{ id = [int64]$f.Id; fullName = [string]$f.FullName }
}

function Invoke-AddFile($mgr) {
    $folderPath = [string](Get-Arg 'folderPath')
    $localPath  = [string](Get-Arg 'localPath')
    $fileName   = [string](Get-Arg 'fileName')
    $comment    = [string](Get-Arg 'comment' '')
    $attachIds  = Get-Arg 'attachmentFileIds' @()
    if (-not (Test-Path $localPath)) { Die ('Local file not found: ' + $localPath) }
    if ($null -eq $script:VdfConn) { Die 'AddFile needs the VDF connection; Connect-Vault did not keep it' }
    $wsFolder = $mgr.DocumentService.GetFolderByPath($folderPath)
    $folder = New-Object Autodesk.DataManagement.Client.Framework.Vault.Currency.Entities.Folder($script:VdfConn, $wsFolder)
    $assocs = @()
    foreach ($id in $attachIds) {
        $a = New-Object Autodesk.Connectivity.WebServices.FileAssocParam
        $a.CldFileId = [int64]$id
        $a.Typ = [Autodesk.Connectivity.WebServices.AssociationType]::Attachment
        $a.RefId = $null
        $a.Source = $null
        $a.ExpectedVaultPath = $null
        $assocs += $a
    }
    $stream = [System.IO.File]::OpenRead($localPath)
    try {
        $fi = $script:VdfConn.FileManager.AddFile(
            $folder, $fileName, $comment, (Get-Item $localPath).LastWriteTime,
            [Autodesk.Connectivity.WebServices.FileAssocParam[]]$assocs, $null,
            [Autodesk.Connectivity.WebServices.FileClassification]::None, $false, $stream)
    } finally { $stream.Dispose() }
    $f = $mgr.DocumentService.GetLatestFileByMasterId([int64]$fi.EntityMasterId)
    return Get-FileOut $f
}

function Invoke-UpdateFileCategories($mgr) {
    $masters = Get-Arg 'masterIds'
    $catId   = Get-Arg 'categoryId'
    $comment = [string](Get-Arg 'comment' '')
    if (-not $masters -or $masters.Count -eq 0) { Die 'UpdateFileCategories requires masterIds' }
    if ($null -eq $catId) { Die 'UpdateFileCategories requires categoryId' }
    $longMasters = [int64[]]($masters | ForEach-Object { [int64]$_ })
    $longCats = [int64[]]($longMasters | ForEach-Object { [int64]$catId })
    $files = $mgr.DocumentServiceExtensions.UpdateFileCategories($longMasters, $longCats, $comment)
    return @{ updated = @($files | ForEach-Object { Get-FileOut $_ }) }
}
```

Add `'GetFileCategories','GetNumberingSchemes','GetFolderByPath'` to `$readOnlyOps`, add one `switch` line per operation (`'AddFile' { Invoke-AddFile $mgr }` and so on for all seven), and list all seven in the header comment.

- [ ] **Step 7: Parse-check the script**

Run: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('scripts/vault_sdk.ps1', [ref]$null, [ref]$e); $e.Count"`
Expected: `0`

- [ ] **Step 8: Commit**

```bash
git add scripts/vault_sdk.ps1 scripts/vault_sdk.py tests/test_vault_sdk_intake_ops.py
git commit -m "Vault SDK bridge: categories, numbering, folders, add file, file categories"
```

---

### Task 2: Live SDK probe (Zak runs it; gate for Task 6 live use)

**Files:**
- Create: `scripts/probes/probe_intake_sdk.py`

**Interfaces:**
- Consumes: every Task 1 wrapper; `VaultRestAPI.get_file_uses`.
- Produces: printed facts the later tasks depend on: the CD numbering scheme name, the exact FILE category names, and the REST `fileAssocType` string for attachments.

- [ ] **Step 1: Write the probe**

```python
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
```

- [ ] **Step 2: Ask Zak to close Vault Explorer and run both commands**

Run: `python scripts/probes/probe_intake_sdk.py` then `python scripts/probes/probe_intake_sdk.py --write`
Expected: scheme list includes the scheme that produces `CD-######`; categories include `Part - Purchased` and `Documents`; `--write` prints two added files, one category update, and a REST `uses` row for `probe.json` whose `fileAssocType` names the attachment type.

If `GenerateFileNumber`, `AddFile` or `UpdateFileCategories` fails here, stop and report the error to the coordinator. Do not continue to Task 6's live run.

- [ ] **Step 3: Record the facts**

Add to `config.json.example` (and tell Zak to add to his `config.json`):

```json
  "intake": {
    "downloads_dir": "",
    "workspace_root": "C:\\Vault Workspace",
    "numbering_scheme": "<scheme name printed by the probe>"
  },
```

Write the printed attachment `fileAssocType` value into `ATTACHMENT_ASSOC` in Task 7 (default there is `"Attachment"`).

- [ ] **Step 4: Commit**

```bash
git add scripts/probes/probe_intake_sdk.py config.json.example
git commit -m "Probe for intake SDK operations; intake config block"
```

---

### Task 3: Spec cards and standard tables

**Files:**
- Create: `part_specs.py`, `standard_specs.json`
- Test: `tests/test_part_specs.py`

**Interfaces:**
- Produces:
  - `SCHEMA_VERSION: int = 1`
  - `REQUIRED_DIMENSIONS: dict[str, tuple[str, ...]]`
  - `validate_card(card: Any, *, require_cd_number: bool = False) -> list[str]` (empty list = valid)
  - `parse_content_center_name(name: str) -> dict | None` → `{"standard", "nominal", "length", "material"}`
  - `card_from_standard(file_name: str, tables: dict | None = None) -> dict | None`
  - `load_standard_tables(path: Path = STANDARD_SPECS_PATH) -> dict`

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_part_specs.py
import copy

import pytest

import part_specs as ps

CARD = {
    "schema": 1,
    "cd_number": None,
    "source": {"vendor": "McMASTER-CARR", "vendor_number": "90510A313",
               "pdf": None, "read_on": "2026-10-09"},
    "type": "t_slot_nut",
    "description": "Fully Threaded T-Slot Nut",
    "material": "Steel", "grade": None, "finish": "Black oxide",
    "thread": {"designation": "M8x1.25", "length": 18, "through": True},
    "dimensions": {"slot_width": 16, "head_width": 25, "head_height": 9,
                   "overall_height": 18, "length": 25},
    "ratings": {"max_temp_c": None, "proof_load_kn": None, "hardness": None},
    "notes": [],
}


def card(**changes):
    c = copy.deepcopy(CARD)
    c.update(changes)
    return c


def test_valid_card_has_no_errors():
    assert ps.validate_card(CARD) == []


def test_missing_top_level_field_is_named():
    c = card()
    del c["grade"]
    assert "missing field: grade" in ps.validate_card(c)


def test_dimension_given_as_text_is_rejected_by_name():
    c = card(dimensions={**CARD["dimensions"], "slot_width": "16 mm"})
    errs = ps.validate_card(c)
    assert any("dimensions.slot_width" in e and "number" in e for e in errs)


def test_required_dimension_for_type():
    dims = dict(CARD["dimensions"])
    del dims["head_height"]
    errs = ps.validate_card(card(dimensions=dims))
    assert "dimensions.head_height is required for type t_slot_nut" in errs


def test_unknown_type_rejected():
    assert any(e.startswith("type must be one of") for e in ps.validate_card(card(type="widget")))


def test_other_type_needs_no_dimensions():
    assert ps.validate_card(card(type="other", dimensions={})) == []


def test_thread_may_be_null_but_not_blank():
    assert ps.validate_card(card(thread=None)) == []
    assert "thread.designation must be a non-empty string" in ps.validate_card(
        card(thread={"designation": " ", "length": None, "through": None}))


def test_require_cd_number():
    assert "cd_number must look like CD-000000" in ps.validate_card(CARD, require_cd_number=True)
    assert ps.validate_card(card(cd_number="CD-001888"), require_cd_number=True) == []


def test_not_an_object():
    assert ps.validate_card([]) == ["spec card must be a JSON object"]


@pytest.mark.parametrize("name,expected", [
    ("ISO 4762 - M8 x 45 - Steel.ipt", {"standard": "ISO 4762", "nominal": "M8", "length": 45.0, "material": "Steel"}),
    ("ISO 7089 - 8 - Stainless Steel", {"standard": "ISO 7089", "nominal": "M8", "length": None, "material": "Stainless Steel"}),
    ("DIN 127 - A 8 - Stainless Steel.ipt", {"standard": "DIN 127", "nominal": "M8", "length": None, "material": "Stainless Steel"}),
    ("ISO 7093-1 - 8 - Steel.ipt", {"standard": "ISO 7093-1", "nominal": "M8", "length": None, "material": "Steel"}),
])
def test_parse_content_center_name(name, expected):
    assert ps.parse_content_center_name(name) == expected


def test_parse_rejects_non_standard_names():
    assert ps.parse_content_center_name("Vibration-Resistant-Spring Lock-Washers-5_16_, M8-Steel.ipt") is None
    assert ps.parse_content_center_name("CD-001888.ipt") is None


def test_card_from_standard_screw():
    c = ps.card_from_standard("ISO 4762 - M8 x 30 - Steel.ipt")
    assert c["type"] == "screw"
    assert c["thread"]["designation"] == "M8x1.25"
    assert c["dimensions"] == {"head_diameter": 13, "head_height": 8, "socket": 6, "length": 30.0}
    assert c["source"] == {"standard": "ISO 4762"}


def test_card_from_standard_washer_aliases_7093():
    c = ps.card_from_standard("ISO 7093 - 8 - Steel.ipt")
    assert c["type"] == "washer"
    assert c["dimensions"] == {"inner_diameter": 8.4, "outer_diameter": 24, "thickness": 2}
    assert c["thread"] is None


def test_card_from_standard_unknown_family_or_size():
    assert ps.card_from_standard("ISO 9999 - M8 - Steel.ipt") is None
    assert ps.card_from_standard("ISO 4762 - M64 x 200 - Steel.ipt") is None
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_part_specs.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'part_specs'`

- [ ] **Step 3: Write `standard_specs.json`**

Values in mm, coarse pitch. Washer `inner_diameter` is d1, `outer_diameter` d2, `thickness` h; DIN 6796 adds `free_height`.

```json
{
  "_comment": "Nominal dimensions (mm) for Content Center families, read by part_specs.card_from_standard. Coarse thread pitch. Add a family by adding a block; sizes not listed return no data.",
  "pitch": {"M4": 0.7, "M5": 0.8, "M6": 1.0, "M8": 1.25, "M10": 1.5, "M12": 1.75},
  "aliases": {"ISO 7093": "ISO 7093-1"},
  "families": {
    "ISO 4762": {"type": "screw", "threaded": true, "sizes": {
      "M4": {"head_diameter": 7, "head_height": 4, "socket": 3},
      "M5": {"head_diameter": 8.5, "head_height": 5, "socket": 4},
      "M6": {"head_diameter": 10, "head_height": 6, "socket": 5},
      "M8": {"head_diameter": 13, "head_height": 8, "socket": 6},
      "M10": {"head_diameter": 16, "head_height": 10, "socket": 8},
      "M12": {"head_diameter": 18, "head_height": 12, "socket": 10}}},
    "ISO 4032": {"type": "nut", "threaded": true, "sizes": {
      "M4": {"width_across_flats": 7, "height": 3.2},
      "M5": {"width_across_flats": 8, "height": 4.7},
      "M6": {"width_across_flats": 10, "height": 5.2},
      "M8": {"width_across_flats": 13, "height": 6.8},
      "M10": {"width_across_flats": 16, "height": 8.4},
      "M12": {"width_across_flats": 18, "height": 10.8}}},
    "ISO 7089": {"type": "washer", "threaded": false, "sizes": {
      "M4": {"inner_diameter": 4.3, "outer_diameter": 9, "thickness": 0.8},
      "M5": {"inner_diameter": 5.3, "outer_diameter": 10, "thickness": 1},
      "M6": {"inner_diameter": 6.4, "outer_diameter": 12, "thickness": 1.6},
      "M8": {"inner_diameter": 8.4, "outer_diameter": 16, "thickness": 1.6},
      "M10": {"inner_diameter": 10.5, "outer_diameter": 20, "thickness": 2},
      "M12": {"inner_diameter": 13, "outer_diameter": 24, "thickness": 2.5}}},
    "ISO 7093-1": {"type": "washer", "threaded": false, "sizes": {
      "M4": {"inner_diameter": 4.3, "outer_diameter": 12, "thickness": 1},
      "M5": {"inner_diameter": 5.3, "outer_diameter": 15, "thickness": 1.2},
      "M6": {"inner_diameter": 6.4, "outer_diameter": 18, "thickness": 1.6},
      "M8": {"inner_diameter": 8.4, "outer_diameter": 24, "thickness": 2},
      "M10": {"inner_diameter": 10.5, "outer_diameter": 30, "thickness": 2.5},
      "M12": {"inner_diameter": 13, "outer_diameter": 37, "thickness": 3}}},
    "DIN 127": {"type": "spring_washer", "threaded": false, "sizes": {
      "M4": {"inner_diameter": 4.1, "outer_diameter": 7.6, "thickness": 0.9},
      "M5": {"inner_diameter": 5.1, "outer_diameter": 9.2, "thickness": 1.2},
      "M6": {"inner_diameter": 6.1, "outer_diameter": 11.8, "thickness": 1.6},
      "M8": {"inner_diameter": 8.2, "outer_diameter": 14.8, "thickness": 2},
      "M10": {"inner_diameter": 10.2, "outer_diameter": 18.1, "thickness": 2.2},
      "M12": {"inner_diameter": 12.2, "outer_diameter": 21.1, "thickness": 2.5}}},
    "DIN 6796": {"type": "spring_washer", "threaded": false, "sizes": {
      "M4": {"inner_diameter": 4.3, "outer_diameter": 9, "thickness": 1, "free_height": 1.3},
      "M5": {"inner_diameter": 5.3, "outer_diameter": 11, "thickness": 1.2, "free_height": 1.55},
      "M6": {"inner_diameter": 6.4, "outer_diameter": 14, "thickness": 1.5, "free_height": 2},
      "M8": {"inner_diameter": 8.4, "outer_diameter": 18, "thickness": 2, "free_height": 2.6},
      "M10": {"inner_diameter": 10.5, "outer_diameter": 23, "thickness": 2.5, "free_height": 3.2},
      "M12": {"inner_diameter": 13, "outer_diameter": 29, "thickness": 3, "free_height": 3.95}}}
  }
}
```

DIN 508 is left out: its thread depends on the slot variant, so a filename cannot pick a row safely. T-slot nuts come from McMaster with spec cards instead.

- [ ] **Step 4: Write `part_specs.py`**

```python
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
```

Note: the `std` normalisation turns `"ISO4762"` and `"ISO 4762"` into `"ISO 4762"`. Check `"ISO 7093-1"` survives it in the test run.

- [ ] **Step 5: Run tests to verify they pass**

Run: `pytest tests/test_part_specs.py -v`
Expected: all passed

- [ ] **Step 6: Commit**

```bash
git add part_specs.py standard_specs.json tests/test_part_specs.py
git commit -m "Spec card validation and standard tables for Content Center parts"
```

---

### Task 4: Intake helpers (pure)

**Files:**
- Create: `mcmaster_intake.py` (helpers only; `register` comes in Task 6)
- Test: `tests/test_mcmaster_intake_helpers.py`

**Interfaces:**
- Produces:
  - `class IntakeError(ValueError)`
  - `LIBRARY_ROOT = "$/DESIGNS/Simplifyber Library/McMaster-Carr"`
  - `FOLDER_BY_TYPE: dict[str, str]`
  - `normalize_pn(pn: str) -> str`
  - `find_downloads(pn: str, downloads_dir: Path) -> dict[str, Path]` → keys `step`, `pdf`
  - `sha256_file(path: Path) -> str`
  - `target_folder(card_type: str, folder: str = "") -> str`
  - `local_dir_for(vault_folder: str, workspace_root: Path) -> Path`
  - `build_iproperties(cd_number: str, pn: str, description: str, finish: str) -> dict` → `{"design": {...}, "custom": {...}}`
  - `file_names(cd_number: str, pn: str) -> dict` → keys `ipt`, `pdf`, `specs`
  - `class PreviewStore(ttl_seconds=900, clock=time.monotonic)` with `put(payload) -> str`, `take(token) -> dict | None`

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_mcmaster_intake_helpers.py
import os
import time
from pathlib import Path

import pytest

import mcmaster_intake as mi


@pytest.mark.parametrize("raw", ["90510A313", " 90510a313 ", "90510 A313"])
def test_normalize_pn(raw):
    assert mi.normalize_pn(raw) == "90510A313"


@pytest.mark.parametrize("raw", ["", "CD-001888", "90510-A313", "abc"])
def test_normalize_pn_rejects(raw):
    with pytest.raises(mi.IntakeError):
        mi.normalize_pn(raw)


def _touch(p: Path, mtime: float) -> Path:
    p.write_bytes(b"x")
    os.utime(p, (mtime, mtime))
    return p


def test_find_downloads_newest_wins(tmp_path):
    _touch(tmp_path / "90510A313_Fully Threaded T-Slot Nut.STEP", 1000)
    newest = _touch(tmp_path / "90510A313_Fully Threaded T-Slot Nut (1).STEP", 2000)
    pdf = _touch(tmp_path / "90510A313.PDF", 1500)
    _touch(tmp_path / "91029A104.STEP", 3000)
    got = mi.find_downloads("90510A313", tmp_path)
    assert got == {"step": newest, "pdf": pdf}


def test_find_downloads_accepts_stp_and_ignores_subfolders(tmp_path):
    (tmp_path / "McMaster intake done").mkdir()
    _touch(tmp_path / "McMaster intake done" / "90510A313.pdf", 9000)
    stp = _touch(tmp_path / "90510a313.stp", 1000)
    pdf = _touch(tmp_path / "90510A313 drawing.pdf", 1000)
    assert mi.find_downloads("90510A313", tmp_path) == {"step": stp, "pdf": pdf}


def test_find_downloads_names_missing_files(tmp_path):
    _touch(tmp_path / "90510A313.pdf", 1000)
    with pytest.raises(mi.IntakeError, match="STEP"):
        mi.find_downloads("90510A313", tmp_path)


def test_target_folder():
    assert mi.target_folder("t_slot_nut") == mi.LIBRARY_ROOT + "/nuts"
    assert mi.target_folder("washer", "washers") == mi.LIBRARY_ROOT + "/washers"
    assert mi.target_folder("other", "t-slot") == mi.LIBRARY_ROOT + "/t-slot"
    with pytest.raises(mi.IntakeError, match="folder"):
        mi.target_folder("other")
    with pytest.raises(mi.IntakeError):
        mi.target_folder("screw", "../escape")


def test_local_dir_for(tmp_path):
    assert mi.local_dir_for(mi.LIBRARY_ROOT + "/nuts", tmp_path) == \
        tmp_path / "DESIGNS" / "Simplifyber Library" / "McMaster-Carr" / "nuts"


def test_build_iproperties():
    p = mi.build_iproperties("CD-001900", "90510A313", "Fully Threaded T-Slot Nut", "Black Oxide")
    assert p["design"] == {"Part Number": "CD-001900", "Description": "Fully Threaded T-Slot Nut",
                           "Vendor": "McMASTER-CARR", "Catalog Web Link": "90510A313", "Designer": "Zak O."}
    assert p["custom"] == {"Vendor Number": "90510A313", "Source": "Buy",
                           "Material Finish": "Black Oxide", "Engr Approved By": "NOT REVIEWED"}


def test_file_names():
    assert mi.file_names("CD-001900", "90510A313") == {
        "ipt": "CD-001900.ipt", "pdf": "CD-001900 McMaster 90510A313.pdf", "specs": "CD-001900.specs.json"}


def test_preview_store_take_once_and_expiry():
    now = [0.0]
    store = mi.PreviewStore(ttl_seconds=10, clock=lambda: now[0])
    t = store.put({"a": 1})
    assert store.take(t) == {"a": 1}
    assert store.take(t) is None
    t2 = store.put({"b": 2})
    now[0] = 11
    assert store.take(t2) is None
    assert store.take("nonsense") is None
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_mcmaster_intake_helpers.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'mcmaster_intake'`

- [ ] **Step 3: Write the helpers** (`mcmaster_intake.py`)

```python
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
```

Note on the design-spec iProperty table: "WEB Link" is Inventor's `Catalog Web Link` in Design Tracking Properties. Material is not set by intake (Inventor material needs a library asset); the spec card carries it. Report both to the coordinator for Zak.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pytest tests/test_mcmaster_intake_helpers.py -v`
Expected: all passed

- [ ] **Step 5: Commit**

```bash
git add mcmaster_intake.py tests/test_mcmaster_intake_helpers.py
git commit -m "McMaster intake helpers: part number, downloads, folders, iProperties, preview tokens"
```

---

### Task 5: Inventor STEP import

**Files:**
- Modify: `scripts/inventor_automation.py` (add after `save_document`, add to `__all__` if present)
- Test: `tests/test_inventor_step_import.py`

**Interfaces:**
- Consumes: `_import_win32()`, `get_inventor_app(*, visible=True)`, `InventorAutomationError`.
- Produces:
  - `PART_DOCUMENT = 12290`
  - `set_iproperties(doc, design: dict[str, str], custom: dict[str, str]) -> None`
  - `import_step_as_part(step_path: str | Path, out_ipt: str | Path, iproperties: dict) -> Path` (iproperties = `build_iproperties(...)` output)

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_inventor_step_import.py
import sys
import types
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import inventor_automation as ia  # noqa: E402


class Prop:
    def __init__(self, value=""):
        self.Value = value


class PropSet:
    def __init__(self, names):
        self.props = {n: Prop() for n in names}

    def Item(self, name):
        if name not in self.props:
            raise KeyError(name)
        return self.props[name]

    def Add(self, value, name):
        self.props[name] = Prop(value)


class Doc:
    def __init__(self, doc_type=ia.PART_DOCUMENT):
        self.DocumentType = doc_type
        self.sets = {
            "Design Tracking Properties": PropSet(["Part Number", "Description", "Vendor", "Catalog Web Link", "Designer"]),
            "Inventor User Defined Properties": PropSet(["Source"]),
        }
        self.PropertySets = types.SimpleNamespace(Item=lambda n: self.sets[n])
        self.saved_as = None
        self.closed = None

    def SaveAs(self, path, copy):
        self.saved_as = (path, copy)
        Path(path).write_bytes(b"ipt")

    def Close(self, skip_save):
        self.closed = skip_save


@pytest.fixture
def inventor(monkeypatch):
    doc = Doc()
    opened = []
    app = types.SimpleNamespace(Documents=types.SimpleNamespace(
        Open=lambda path, visible: opened.append((path, visible)) or doc))
    com = types.SimpleNamespace(CoInitialize=lambda: None, CoUninitialize=lambda: None)
    monkeypatch.setattr(ia, "_import_win32", lambda: (com, None))
    monkeypatch.setattr(ia, "get_inventor_app", lambda visible=True: app)
    return doc, opened


IPROPS = {"design": {"Part Number": "CD-001900", "Vendor": "McMASTER-CARR"},
          "custom": {"Vendor Number": "90510A313", "Source": "Buy"}}


def test_import_sets_properties_and_saves(tmp_path, inventor):
    doc, opened = inventor
    step = tmp_path / "90510A313.STEP"
    step.write_bytes(b"step")
    out = tmp_path / "CD-001900.ipt"
    assert ia.import_step_as_part(step, out, IPROPS) == out
    assert opened == [(str(step), False)]
    dt = doc.sets["Design Tracking Properties"].props
    ud = doc.sets["Inventor User Defined Properties"].props
    assert dt["Part Number"].Value == "CD-001900"
    assert ud["Vendor Number"].Value == "90510A313"   # added
    assert ud["Source"].Value == "Buy"                 # existing, overwritten
    assert doc.saved_as == (str(out), False)
    assert doc.closed is True


def test_refuses_to_overwrite(tmp_path, inventor):
    step = tmp_path / "a.step"
    step.write_bytes(b"s")
    out = tmp_path / "CD-001900.ipt"
    out.write_bytes(b"old")
    with pytest.raises(ia.InventorAutomationError, match="overwrite"):
        ia.import_step_as_part(step, out, IPROPS)


def test_missing_step(tmp_path, inventor):
    with pytest.raises(ia.InventorAutomationError, match="not found"):
        ia.import_step_as_part(tmp_path / "nope.step", tmp_path / "x.ipt", IPROPS)


def test_assembly_step_is_rejected_and_closed(tmp_path, inventor):
    doc, _ = inventor
    doc.DocumentType = 12291
    step = tmp_path / "a.step"
    step.write_bytes(b"s")
    with pytest.raises(ia.InventorAutomationError, match="assembly"):
        ia.import_step_as_part(step, tmp_path / "x.ipt", IPROPS)
    assert doc.closed is True
    assert not (tmp_path / "x.ipt").exists()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_inventor_step_import.py -v`
Expected: FAIL with `AttributeError: module 'inventor_automation' has no attribute 'PART_DOCUMENT'`

- [ ] **Step 3: Implement**

```python
PART_DOCUMENT = 12290  # Inventor DocumentTypeEnum.kPartDocumentObject
_DESIGN_TRACKING = "Design Tracking Properties"
_USER_DEFINED = "Inventor User Defined Properties"


def set_iproperties(doc, design: dict[str, str], custom: dict[str, str]) -> None:
    """Write Design Tracking iProperties and create-or-update custom ones."""
    dt = doc.PropertySets.Item(_DESIGN_TRACKING)
    for name, value in design.items():
        dt.Item(name).Value = value
    ud = doc.PropertySets.Item(_USER_DEFINED)
    for name, value in custom.items():
        try:
            ud.Item(name).Value = value
        except Exception:
            ud.Add(value, name)


def import_step_as_part(step_path: str | Path, out_ipt: str | Path, iproperties: dict) -> Path:
    """Open a STEP in Inventor as a part, set iProperties, save as out_ipt.

    Safe on a worker thread (initialises COM itself). Refuses to overwrite an
    existing .ipt and rejects STEPs that open as assemblies."""
    step, out = Path(step_path), Path(out_ipt)
    if not step.is_file():
        raise InventorAutomationError(f"STEP file not found: {step}")
    if out.exists():
        raise InventorAutomationError(f"Refusing to overwrite existing file {out}")
    pythoncom, _ = _import_win32()
    pythoncom.CoInitialize()
    try:
        app = get_inventor_app(visible=True)
        doc = app.Documents.Open(str(step), False)
        try:
            if doc.DocumentType != PART_DOCUMENT:
                raise InventorAutomationError(
                    f"{step.name} opened as an assembly, not a single part. Import it by hand.")
            set_iproperties(doc, iproperties.get("design", {}), iproperties.get("custom", {}))
            out.parent.mkdir(parents=True, exist_ok=True)
            doc.SaveAs(str(out), False)
        finally:
            doc.Close(True)
        del doc, app
    finally:
        pythoncom.CoUninitialize()
    return out
```

`visible=True` on purpose: `get_inventor_app` sets `app.Visible`, and Zak's running Inventor must not disappear.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pytest tests/test_inventor_step_import.py -v`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add scripts/inventor_automation.py tests/test_inventor_step_import.py
git commit -m "Inventor: import a STEP as a part with iProperties"
```

---

### Task 6: Preview and commit tools

**Files:**
- Modify: `mcmaster_intake.py` (add `find_by_vendor_number`, `register`)
- Test: `tests/test_mcmaster_intake_tools.py`

**Interfaces:**
- Consumes: Task 1 SDK wrappers (via `vault_write_tools._sdk()`), Task 3 `validate_card`, Task 4 helpers, Task 5 `import_step_as_part`, `vault_write_tools._dump`, `check_file_properties.fetch_file(api, vault_id, file_name)` and `evaluate_against_rules(properties, raw_category, rules)`, `check_item_properties.load_json(path)`, `VaultRestAPI.get_property_definitions` and `advanced_search`.
- Produces:
  - `async find_by_vendor_number(api, vault: str, pn: str) -> dict | None` → `{"name", "file_id", "folder"}`
  - `register(mcp, api, resolved_vault, *, config: dict | None = None, store: PreviewStore | None = None, inventor=None, sleep=asyncio.sleep) -> None` registering `vault_mcmaster_intake_preview` and `vault_mcmaster_intake_commit` (and, in Task 7, `vault_get_part_specs`)
  - Commit result: `{"applied": True, "verified": bool, "cd_number", "vault_path", "problems": [str], "steps": [...]}` or `{"applied": False, "failed_step": str, "error": str, "already_in_vault": [...], "steps": [...]}`

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_mcmaster_intake_tools.py
import asyncio
import copy
import json
import os
import sys
import types
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))

import mcmaster_intake as mi  # noqa: E402
from tests.test_part_specs import CARD  # noqa: E402


class FakeMCP:
    def __init__(self):
        self.tools = {}

    def tool(self):
        def deco(fn):
            self.tools[fn.__name__] = fn
            return fn
        return deco


class FakeAPI:
    def __init__(self, existing=None, props=None):
        self.existing = existing or []
        self.props = props
        self.searches = []

    async def get_property_definitions(self, vault_id, limit=200, cursor_state=None):
        return {"error": False, "data": {"results": [{"id": 77, "displayName": "Vendor Number", "systemName": "VendorNumber"}]}}

    async def advanced_search(self, vault_id, body, **kw):
        self.searches.append(body)
        return {"error": False, "data": {"results": self.existing}}


class FakeSDK(types.ModuleType):
    def __init__(self, fail_on=None):
        super().__init__("vault_sdk")
        self.calls = []
        self.fail_on = fail_on
        self.next_id = 500

    def _rec(self, op, *a):
        self.calls.append((op, a))
        if op == self.fail_on:
            raise RuntimeError(f"{op} boom")

    def get_folder_by_path(self, path):
        self._rec("get_folder_by_path", path)
        return {"found": path.endswith("/nuts"), "id": 1, "fullName": path, "isLibrary": True}

    def get_numbering_schemes(self):
        self._rec("get_numbering_schemes")
        return {"schemes": [{"id": 3, "name": "CD Parts", "isDefault": True}]}

    def generate_file_number(self, scheme_id):
        self._rec("generate_file_number", scheme_id)
        return {"number": "CD-001900"}

    def add_folder(self, parent, name):
        self._rec("add_folder", parent, name)
        return {"id": 2, "fullName": f"{parent}/{name}"}

    def add_file(self, folder, local, name, *, attachment_file_ids=(), comment=""):
        self._rec("add_file", folder, name, list(attachment_file_ids))
        self.next_id += 10
        return {"id": self.next_id, "masterId": self.next_id - 1, "name": name, "checkedOut": False}

    def get_file_categories(self):
        self._rec("get_file_categories")
        return {"categories": [{"id": 1, "name": "Part - Purchased"}, {"id": 2, "name": "Documents"}]}

    def update_file_categories(self, ids, cat, *, comment=""):
        self._rec("update_file_categories", list(ids), cat)
        return {"updated": []}


class FakeInventor:
    def __init__(self):
        self.calls = []

    def import_step_as_part(self, step, out, iprops):
        self.calls.append((Path(step).name, Path(out), iprops))
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        Path(out).write_bytes(b"ipt")
        return Path(out)


GOOD_PROPS = {"Category Name": "Part - Purchased", "State": "Work in Progress", "File Name": "CD-001900.ipt",
              "Source": "Buy", "Vendor": "McMASTER-CARR", "Vendor Number": "90510A313"}


@pytest.fixture
def env(tmp_path, monkeypatch):
    dl = tmp_path / "Downloads"
    dl.mkdir()
    ws = tmp_path / "ws"
    (dl / "90510A313_Fully Threaded T-Slot Nut.STEP").write_bytes(b"step")
    (dl / "90510A313.pdf").write_bytes(b"%PDF")
    sdk = FakeSDK()
    monkeypatch.setitem(sys.modules, "vault_sdk", sdk)
    fetched = {"props": dict(GOOD_PROPS)}

    async def fake_fetch(api, vault_id, file_name):
        return {"properties": dict(fetched["props"]), "note": ""}

    import check_file_properties
    monkeypatch.setattr(check_file_properties, "fetch_file", fake_fetch)
    api = FakeAPI()
    inv = FakeInventor()
    mcp = FakeMCP()

    async def no_sleep(_):
        return None

    mi.register(mcp, api, lambda v: "1",
                config={"downloads_dir": str(dl), "workspace_root": str(ws), "numbering_scheme": "CD Parts"},
                inventor=inv, sleep=no_sleep)
    return types.SimpleNamespace(tools=mcp.tools, sdk=sdk, api=api, inv=inv, dl=dl, ws=ws, fetched=fetched)


def run(coro):
    return json.loads(asyncio.run(coro))


def preview(env, pn="90510A313", card=None, **kw):
    return run(env.tools["vault_mcmaster_intake_preview"](
        mcmaster_pn=pn, spec_card_json=json.dumps(card or CARD),
        description="Fully Threaded T-Slot Nut", finish="Black Oxide", **kw))


def commit(env, token):
    return run(env.tools["vault_mcmaster_intake_commit"](token=token))


def test_preview_writes_nothing_and_returns_token(env):
    out = preview(env)
    assert out["preview"] is True and out["token"]
    assert out["folder"] == mi.LIBRARY_ROOT + "/nuts"
    assert out["new_folder"] is False
    assert out["files"]["step"].endswith(".STEP")
    assert out["category"] == "Part - Purchased"
    assert [c[0] for c in env.sdk.calls] == ["get_folder_by_path"]


def test_preview_duplicate_stops(env):
    env.api.existing = [{"name": "CD-001888.ipt", "id": 127721, "file": {"id": 127685}}]
    out = preview(env, pn=" 90510a313 ")
    assert out["duplicate"] is True
    assert "CD-001888.ipt" in out["message"]
    crit = env.api.searches[0]["searchCriterias"][0]
    assert crit["searchString"] == "90510A313" and crit["operator"] == "IsExactly"


def test_preview_rejects_bad_card_by_field(env):
    bad = copy.deepcopy(CARD)
    bad["dimensions"]["slot_width"] = "16 mm"
    out = preview(env, card=bad)
    assert out["error"] is True
    assert any("dimensions.slot_width" in e for e in out["problems"])


def test_preview_rejects_card_for_other_part(env):
    other = copy.deepcopy(CARD)
    other["source"]["vendor_number"] = "91029A104"
    out = preview(env, card=other)
    assert out["error"] is True and "91029A104" in out["message"]


def test_commit_happy_path_order_and_names(env):
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True and out["verified"] is True, out
    assert out["cd_number"] == "CD-001900"
    ops = [c[0] for c in env.sdk.calls]
    assert ops == ["get_folder_by_path", "get_numbering_schemes", "generate_file_number",
                   "add_file", "add_file", "add_file", "get_file_categories",
                   "update_file_categories", "update_file_categories"]
    adds = [c[1] for c in env.sdk.calls if c[0] == "add_file"]
    assert [a[1] for a in adds] == ["CD-001900 McMaster 90510A313.pdf", "CD-001900.specs.json", "CD-001900.ipt"]
    assert adds[2][2] == [510, 520]          # pdf + specs attached to the part
    cats = [c[1] for c in env.sdk.calls if c[0] == "update_file_categories"]
    assert cats == [([529], 1), ([509, 519], 2)]
    local = mi.local_dir_for(mi.LIBRARY_ROOT + "/nuts", env.ws)
    written = json.loads((local / "CD-001900.specs.json").read_text(encoding="utf-8"))
    assert written["cd_number"] == "CD-001900"
    assert not list(env.dl.glob("90510A313*"))
    assert len(list((env.dl / mi.DONE_FOLDER).iterdir())) == 2


def test_commit_token_used_once(env):
    token = preview(env)["token"]
    commit(env, token)
    again = commit(env, token)
    assert again["error"] is True and "preview" in again["message"].lower()
    assert sum(1 for c in env.sdk.calls if c[0] == "generate_file_number") == 1


def test_commit_refuses_changed_download(env):
    token = preview(env)["token"]
    (env.dl / "90510A313.pdf").write_bytes(b"%PDF changed")
    out = commit(env, token)
    assert out["applied"] is False and out["failed_step"] == "recheck"
    assert not any(c[0] in ("generate_file_number", "add_file") for c in env.sdk.calls)


def test_commit_stops_on_failure_and_lists_written(env, monkeypatch):
    token = preview(env)["token"]
    env.sdk.fail_on = "update_file_categories"
    out = commit(env, token)
    assert out["applied"] is False and out["failed_step"] == "category"
    assert [w["name"] for w in out["already_in_vault"]] == [
        "CD-001900 McMaster 90510A313.pdf", "CD-001900.specs.json", "CD-001900.ipt"]


def test_commit_creates_new_folder_when_named(env):
    other = copy.deepcopy(CARD)
    other["type"] = "other"
    out = preview(env, card=other, folder="t-slot")
    assert out["new_folder"] is True
    res = commit(env, out["token"])
    assert ("add_folder", (mi.LIBRARY_ROOT, "t-slot")) in env.sdk.calls
    assert res["applied"] is True


def test_commit_reports_rule_and_category_failures(env):
    env.fetched["props"] = {**GOOD_PROPS, "Category Name": "Unclassified", "Source": ""}
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True and out["verified"] is False
    assert any("Unclassified" in p for p in out["problems"])


def test_commit_rejects_bad_generated_number(env, monkeypatch):
    monkeypatch.setattr(env.sdk, "generate_file_number", lambda sid: {"number": "PART-12"})
    out = commit(env, preview(env)["token"])
    assert out["applied"] is False and out["failed_step"] == "number"
```

Note for the implementer: `test_commit_reports_rule_and_category_failures` sets `Category Name` to Unclassified, so the rules resolve no category; the problem list must still contain the category mismatch line. `test_commit_happy_path_order_and_names` relies on the real `file_property_rules.json` passing `GOOD_PROPS` for Part - Purchased; if the live rules require more, extend `GOOD_PROPS`, not the code.

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_mcmaster_intake_tools.py -v`
Expected: FAIL with `AttributeError: module 'mcmaster_intake' has no attribute 'register'`

- [ ] **Step 3: Implement `find_by_vendor_number` and `register`** (append to `mcmaster_intake.py`; add `import asyncio, json, shutil` and `from datetime import date` to the imports)

```python
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
                    if "Vendor Number" in (d.get("displayName"), d.get("systemName"))), None)
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
```

Check `check_file_properties` imports cleanly inside the test process (it inserts `scripts/` on `sys.path`; `mcp_server` already does). If `vault_slim` record keys differ from `displayName`/`systemName`, match the logic at `mcp_server.py:526-534` and keep the FakeAPI in step.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pytest tests/test_mcmaster_intake_tools.py tests/test_mcmaster_intake_helpers.py -v`
Expected: all passed

- [ ] **Step 5: Commit**

```bash
git add mcmaster_intake.py tests/test_mcmaster_intake_tools.py
git commit -m "McMaster intake: preview and commit tools with verify and stop-on-failure"
```

---

### Task 7: `vault_get_part_specs` tool

**Files:**
- Modify: `mcmaster_intake.py` (inside `register`, plus module helpers)
- Test: `tests/test_part_specs_tool.py`

**Interfaces:**
- Consumes: `vault_write_tools.find_file(api, vault, ref) -> (fv | None, err)`, `vault_slim.file_version(fv)`, `api.get_file_uses(vault_id, file_version_id, limit=...)` rows `{parentFile, childFile, fileAssocType}`, `api.download_file_version_content(vault_id, file_version_id)` (`data` is bytes), Task 3 `card_from_standard`.
- Produces: tool `vault_get_part_specs(part: str, vault_id_param: str = "") -> str` returning `{"part": str, "specs": [card + {"file": name}], "no_data": [{"file", "reason"}]}`; module constant `ATTACHMENT_ASSOC = "Attachment"` (value confirmed by the Task 2 probe), `DEPENDENCY_ASSOC = "Dependency"`.

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_part_specs_tool.py
import asyncio
import json
import sys

import pytest

import mcmaster_intake as mi
from tests.test_mcmaster_intake_tools import FakeMCP, FakeSDK
from tests.test_part_specs import CARD


def fv(name, vid, category="Part - Purchased", folder="$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts"):
    return {"id": vid, "name": name, "category": {"name": category}, "file": {"id": vid - 1},
            "parentFolder": {"fullName": folder}}


class SpecsAPI:
    def __init__(self):
        card = dict(CARD, cd_number="CD-001888")
        self.files = {
            "CD-001887.iam": fv("CD-001887.iam", 100, "Assembly - Engineering", "$/DESIGNS/PRODUCTION EQUIPMENT"),
            "CD-001888.ipt": fv("CD-001888.ipt", 200),
        }
        self.uses = {
            "100": [("CD-001888.ipt", 200, "Dependency"),
                    ("ISO 4762 - M8 x 30 - Steel.ipt", 300, "Dependency"),
                    ("CD-001888.ipt", 200, "Dependency"),
                    ("CD-001886.ipt", 400, "Dependency"),
                    ("Odd Washer.ipt", 500, "Dependency")],
            "200": [("CD-001888.specs.json", 210, "Attachment"), ("CD-001888 McMaster 90510A313.pdf", 220, "Attachment")],
            "300": [], "400": [], "500": [],
        }
        self.content = {"210": json.dumps(card).encode()}
        self.cats = {300: ("Part - Content Center", "$/Content Center Files/en-US/ISO 4762"),
                     500: ("Part - Content Center", "$/Content Center Files/en-US/Odd")}

    async def search_files(self, vault_id, query, latest_only=True, limit=20):
        f = self.files.get(query)
        return {"error": False, "data": {"results": [f] if f else []}}

    async def get_file_by_id(self, vault_id, file_id, released_only=False):
        f = next(v for v in self.files.values() if str(v["id"]) == str(file_id) or str(v["file"]["id"]) == str(file_id))
        return {"error": False, "data": {"fileVersion": f}}

    async def get_file_uses(self, vault_id, file_version_id, limit=200, **kw):
        rows = []
        for name, vid, assoc in self.uses[str(file_version_id)]:
            cat, folder = self.cats.get(vid, ("Part - Purchased", "$/DESIGNS/Simplifyber Library/McMaster-Carr"))
            rows.append({"parentFile": {"id": file_version_id}, "fileAssocType": assoc,
                         "childFile": fv(name, vid, cat, folder)})
        return {"error": False, "data": {"results": rows}}

    async def download_file_version_content(self, vault_id, file_version_id, timeout=180.0):
        return {"error": False, "data": self.content[str(file_version_id)]}


@pytest.fixture
def tool(monkeypatch, tmp_path):
    monkeypatch.setitem(sys.modules, "vault_sdk", FakeSDK())
    mcp = FakeMCP()
    mi.register(mcp, SpecsAPI(), lambda v: "1", config={"downloads_dir": str(tmp_path)})
    return mcp.tools["vault_get_part_specs"]


def run(coro):
    return json.loads(asyncio.run(coro))


def test_single_purchased_part(tool):
    out = run(tool(part="CD-001888.ipt"))
    assert out["no_data"] == []
    assert out["specs"][0]["file"] == "CD-001888.ipt"
    assert out["specs"][0]["dimensions"]["slot_width"] == 16


def test_assembly_walk_unique_parts_and_gaps(tool):
    out = run(tool(part="CD-001887.iam"))
    assert sorted(s["file"] for s in out["specs"]) == ["CD-001888.ipt", "ISO 4762 - M8 x 30 - Steel.ipt"]
    gaps = {g["file"]: g["reason"] for g in out["no_data"]}
    assert "no spec card attached" in gaps["CD-001886.ipt"]
    assert "not in standard_specs.json" in gaps["Odd Washer.ipt"]


def test_unknown_part(tool):
    out = run(tool(part="CD-999999.ipt"))
    assert out["error"] is True
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `pytest tests/test_part_specs_tool.py -v`
Expected: FAIL with `KeyError: 'vault_get_part_specs'`

- [ ] **Step 3: Implement** (module helpers, then the tool at the end of `register`)

```python
ATTACHMENT_ASSOC = "Attachment"   # REST fileAssocType for attachments; confirmed by scripts/probes/probe_intake_sdk.py
DEPENDENCY_ASSOC = "Dependency"
MAX_PARTS = 500


def _is_content_center(f: dict[str, Any]) -> bool:
    return (f.get("category") == "Part - Content Center"
            or str(f.get("folder") or "").startswith("$/Content Center Files"))


async def _uses(api, vault: str, file_version_id: str) -> list[dict[str, Any]]:
    import vault_slim as slim
    res = await api.get_file_uses(vault_id=vault, file_version_id=str(file_version_id), limit=500)
    if res.get("error"):
        raise IntakeError(f"Could not read uses of file version {file_version_id}: {res.get('data')}")
    return slim._records(res.get("data"))


async def _spec_for(api, vault: str, f: dict[str, Any]) -> tuple[Optional[dict], str]:
    from part_specs import card_from_standard
    if _is_content_center(f):
        card = card_from_standard(f["name"])
        return (card, "") if card else (None, "Content Center family or size not in standard_specs.json")
    for row in await _uses(api, vault, f["file_version_id"]):
        child = row.get("childFile") or {}
        if row.get("fileAssocType") == ATTACHMENT_ASSOC and str(child.get("name", "")).endswith(".specs.json"):
            res = await api.download_file_version_content(vault_id=vault, file_version_id=str(child["id"]))
            if res.get("error"):
                return None, f"spec card download failed: {res.get('data')}"
            try:
                return json.loads(res["data"]), ""
            except ValueError as exc:
                return None, f"spec card is not valid JSON: {exc}"
    return None, "no spec card attached"


async def _collect_parts(api, vault: str, root: dict[str, Any]) -> list[dict[str, Any]]:
    """Unique .ipt files under an assembly (recursive), or [root] for a part."""
    import vault_slim as slim
    if not root["name"].lower().endswith(".iam"):
        return [root]
    parts: dict[str, dict[str, Any]] = {}
    seen: set[str] = set()
    queue = [root]
    while queue and len(parts) < MAX_PARTS:
        asm = queue.pop()
        if asm["file_version_id"] in seen:
            continue
        seen.add(asm["file_version_id"])
        for row in await _uses(api, vault, asm["file_version_id"]):
            if row.get("fileAssocType") != DEPENDENCY_ASSOC:
                continue
            child = slim.file_version(row.get("childFile") or {})
            name = str(child.get("name", ""))
            if name.lower().endswith(".iam"):
                queue.append(child)
            elif name.lower().endswith(".ipt"):
                parts.setdefault(name, child)
    return list(parts.values())
```

Inside `register`, after the commit tool:

```python
    @mcp.tool()
    async def vault_get_part_specs(part: str, vault_id_param: str = "") -> str:
        """Spec cards for a part, or for every part in an assembly.

        Purchased parts return the <CD>.specs.json attached at intake.
        Content Center parts (ISO/DIN) return nominal dimensions from
        standard_specs.json. Parts with neither are listed under no_data.
        Use this for stackup and interface checks.

        Args:
            part: File name (CD-001887.iam, CD-001888.ipt) or file id.
            vault_id_param: Vault ID. Leave empty for the configured vault.
        """
        import vault_slim as slim
        from vault_write_tools import find_file
        vault = resolved_vault(vault_id_param)
        fv_raw, problem = await find_file(api, vault, part)
        if problem or fv_raw is None:
            return err(problem or f"{part} not found")
        root = slim.file_version(fv_raw)
        try:
            files = await _collect_parts(api, vault, root)
            specs, no_data = [], []
            for f in files:
                card, reason = await _spec_for(api, vault, f)
                if card is None:
                    no_data.append({"file": f["name"], "reason": reason})
                else:
                    specs.append({"file": f["name"], **card})
        except IntakeError as exc:
            return err(str(exc))
        return _dump({"part": root["name"], "specs": specs, "no_data": no_data})
```

If `slim.file_version` reads category and folder from keys other than the test's `category.name` / `parentFolder.fullName`, change the test's `fv()` builder to the real REST shape (see `tests/fixtures/vault_file_cd001659.json`), not the code.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pytest tests/test_part_specs_tool.py -v`
Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add mcmaster_intake.py tests/test_part_specs_tool.py
git commit -m "vault_get_part_specs: spec cards for parts and assemblies"
```

---

### Task 8: Wire into the server and run the whole suite

**Files:**
- Modify: `mcp_server.py:2133-2146` (registration block), `README.md` (tool list)
- Test: `tests/test_smoke.py` (extend if it lists tool names)

**Interfaces:**
- Consumes: `mcmaster_intake.register(mcp, api, resolved_vault)`.

- [ ] **Step 1: Write the failing test** (append to `tests/test_smoke.py`, or create `tests/test_intake_registration.py` if the smoke test does not build the server)

```python
def test_intake_tools_registered():
    from mcp_server import create_mcp_server

    class Api:  # create_mcp_server only stores it
        pass

    mcp = create_mcp_server(api=Api(), vault_id="1")
    names = {t.name for t in asyncio.run(mcp.list_tools())}
    assert {"vault_mcmaster_intake_preview", "vault_mcmaster_intake_commit", "vault_get_part_specs"} <= names
```

(add `import asyncio` at the top of the file if missing)

- [ ] **Step 2: Run it to verify it fails**

Run: `pytest tests/test_intake_registration.py -v`
Expected: FAIL, the three names are missing

- [ ] **Step 3: Register** (after `vault_cad_tools.register(...)` in `mcp_server.py`)

```python
    import mcmaster_intake
    mcmaster_intake.register(mcp, api, _resolved_vault)
```

Add the three tools to the README tool list with one line each, and a short "McMaster intake" section: download STEP + PDF to Downloads, ask Claude, approve the preview, close Vault Explorer before commit.

- [ ] **Step 4: Run the whole suite**

Run: `pytest -q`
Expected: all passed, no new warnings about live Vault access

- [ ] **Step 5: Commit**

```bash
git add mcp_server.py README.md tests/test_intake_registration.py
git commit -m "Register McMaster intake and part specs tools"
```

- [ ] **Step 6: Live run with Zak (after Task 2 probe passed)**

Restart the vault MCP server. With Zak: pick one CD-001887 replacement part (for example the DIN 6796 M8 conical washer from McMaster), Zak downloads STEP + PDF, run preview, Zak approves, run commit. Zak checks in Vault Explorer: number, folder, category Part - Purchased, two attachments, iProperties, and `check_file_properties.py <CD>.ipt` passes. Then run `vault_get_part_specs("CD-001887.iam")` and confirm the new part appears with its card. Report the result; do not claim success without Zak's check.
