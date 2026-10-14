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
        self.file_hits = []
        self.file_queries = []

    async def search_files(self, vault_id, query, **kw):
        self.file_queries.append((query, kw))
        return {"error": False, "data": {"results": list(self.file_hits)}}

    async def get_property_definitions(self, vault_id, limit=200, cursor_state=None):
        return {"error": False, "data": {"results": [{"id": 77, "displayName": "Vendor Number", "systemName": "VendorNumber"}]}}

    async def advanced_search(self, vault_id, body, **kw):
        self.searches.append(body)
        return {"error": False, "data": {"results": self.existing}}


class FakeSDK(types.ModuleType):
    def __init__(self, fail_on=None):
        super().__init__("vault_bridge")
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
    def __init__(self, not_running=None):
        self.calls = []
        self.not_running = not_running
        self.checks = 0

    def check_inventor_running(self):
        self.checks += 1
        if self.not_running:
            raise RuntimeError(self.not_running)

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
    monkeypatch.setitem(sys.modules, "vault_bridge", sdk)
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


def test_commit_verify_exception_becomes_problem(env, monkeypatch):
    import check_file_properties

    async def boom(api, vault_id, file_name):
        raise RuntimeError("network down")

    monkeypatch.setattr(check_file_properties, "fetch_file", boom)
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True and out["verified"] is False
    assert out["cd_number"] == "CD-001900" and out["vault_path"].endswith("CD-001900.ipt")
    assert any("verify failed" in p and "network down" in p for p in out["problems"])


def test_commit_tidy_exception_still_applied(env, monkeypatch):
    def boom(src, dst):
        raise PermissionError("file in use")

    monkeypatch.setattr(mi.shutil, "move", boom)
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True and out["cd_number"] == "CD-001900"
    assert any("tidy failed" in p and "file in use" in p for p in out["problems"])
    assert {"step": "tidy", "ok": False} .items() <= next(s for s in out["steps"] if s["step"] == "tidy").items()


def test_commit_fails_when_generated_cd_already_in_vault(env):
    token = preview(env)["token"]
    env.api.file_hits = [{"name": "CD-001900.iam", "id": 9}, {"name": "CD-0019001.ipt", "id": 10}]
    out = commit(env, token)
    assert out["applied"] is False and out["failed_step"] == "number"
    assert "CD-001900.iam" in out["error"] and "behind" in out["error"]
    assert "CD-0019001.ipt" not in out["error"]
    assert env.api.file_queries[0][0] == "CD-001900"
    assert env.api.file_queries[0][1] == {"latest_only": True, "limit": 20}
    assert not any(c[0] in ("add_file", "add_folder") for c in env.sdk.calls)


def test_commit_ignores_search_noise_for_cd(env):
    env.api.file_hits = [{"name": "CD-0019001.ipt", "id": 10}]
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True


def test_failed_part_add_reports_uncertain_and_cd(env):
    token = preview(env)["token"]
    orig = env.sdk.add_file

    def flaky(folder, local, name, **kw):
        if name == "CD-001900.ipt":
            raise TimeoutError("reply lost")
        return orig(folder, local, name, **kw)

    env.sdk.add_file = flaky
    out = commit(env, token)
    assert out["failed_step"] == "part"
    assert out["cd_number"] == "CD-001900"
    assert out["uncertain"] == ["CD-001900.ipt"]


def test_bridge_timeout_on_add_file_is_uncertain_without_licence_note(env):
    import vault_bridge

    token = preview(env)["token"]
    orig = env.sdk.add_file

    def timed_out(folder, local, name, **kw):
        if name == "CD-001900.ipt":
            raise vault_bridge.BridgeError("timeout", "no reply in time")
        return orig(folder, local, name, **kw)

    env.sdk.add_file = timed_out
    out = commit(env, token)
    assert "CD-001900.ipt" in out["uncertain"]
    assert "license" not in out


def test_failed_category_reports_uncertain_names(env):
    token = preview(env)["token"]
    env.sdk.fail_on = "update_file_categories"
    out = commit(env, token)
    assert out["uncertain"] == ["CD-001900.ipt"]


def test_failed_before_number_has_no_cd_or_uncertain(env):
    token = preview(env)["token"]
    (env.dl / "90510A313.pdf").write_bytes(b"changed")
    out = commit(env, token)
    assert "cd_number" not in out and out["uncertain"] == []


def test_licence_note_after_partial_write_does_not_claim_nothing_changed(env):
    token = preview(env)["token"]
    orig = env.sdk.update_file_categories

    def lic(ids, cat, *, comment=""):
        raise RuntimeError("Failed to acquire a license")

    env.sdk.update_file_categories = lic
    out = commit(env, token)
    assert "Nothing was changed" not in out["license"]
    assert "already_in_vault" in out["license"]


def test_licence_note_original_when_nothing_written(env):
    token = preview(env)["token"]
    env.sdk.get_numbering_schemes = lambda: (_ for _ in ()).throw(RuntimeError("Failed to acquire a license"))
    out = commit(env, token)
    assert "Nothing was changed" in out["license"]


def test_inventor_check_failure_stops_before_number(env):
    token = preview(env)["token"]
    env.inv.not_running = "Inventor is not running. Start Inventor"
    out = commit(env, token)
    assert out["applied"] is False and out["failed_step"] == "inventor check"
    assert "not running" in out["error"]
    assert not any(c[0] in ("generate_file_number", "add_file", "add_folder") for c in env.sdk.calls)
    assert env.inv.checks == 1 and env.inv.calls == []


def test_inventor_check_is_first_step(env):
    out = commit(env, preview(env)["token"])
    assert [s["step"] for s in out["steps"]][:3] == ["inventor check", "recheck", "number"]


def test_preview_refuses_pn_already_committed_this_session(env):
    out = commit(env, preview(env)["token"])
    assert out["applied"] is True
    (env.dl / "90510A313.STEP").write_bytes(b"s")
    (env.dl / "90510A313x.pdf").write_bytes(b"p")
    again = preview(env)
    assert again["error"] is True
    assert "already committed in this session as CD-001900" in again["message"]


def test_preview_refuses_pn_after_failed_commit_past_number(env):
    token = preview(env)["token"]
    env.sdk.fail_on = "update_file_categories"
    assert commit(env, token)["applied"] is False
    again = preview(env)
    assert again["error"] is True and "CD-001900" in again["message"]


def test_second_token_for_same_pn_refused_at_commit(env):
    t1 = preview(env)["token"]
    t2 = preview(env)["token"]
    assert commit(env, t1)["applied"] is True
    out = commit(env, t2)
    assert out["error"] is True and "already committed" in out["message"]
    assert sum(1 for c in env.sdk.calls if c[0] == "generate_file_number") == 1


def test_pn_is_in_progress_marker_blocks_preview(env):
    t1 = preview(env)["token"]
    env.inv.not_running = None
    seen = {}
    orig = env.inv.check_inventor_running

    def spy():
        seen["msg"] = preview(env)
    env.inv.check_inventor_running = spy
    commit(env, t1)
    assert "in progress" in seen["msg"]["message"]


def test_pn_released_when_commit_fails_at_inventor_check(env):
    token = preview(env)["token"]
    env.inv.not_running = "Inventor is not running."
    assert commit(env, token)["failed_step"] == "inventor check"
    env.inv.not_running = None
    assert preview(env)["preview"] is True


def test_pn_released_when_commit_fails_at_recheck(env):
    token = preview(env)["token"]
    (env.dl / "90510A313.pdf").write_bytes(b"changed")
    assert commit(env, token)["failed_step"] == "recheck"
    assert preview(env)["preview"] is True


def test_commit_refuses_duplicate_added_since_preview(env):
    token = preview(env)["token"]
    env.api.existing = [{"name": "CD-001899.ipt", "id": 5, "file": {"id": 4}}]
    out = commit(env, token)
    assert out["applied"] is False and out["failed_step"] == "recheck"
    assert "CD-001899.ipt" in out["error"]
    assert not any(c[0] in ("generate_file_number", "add_file", "add_folder") for c in env.sdk.calls)


def test_tidy_failure_does_not_flip_verified(env, monkeypatch):
    def boom(src, dst):
        raise PermissionError("file in use")

    monkeypatch.setattr(mi.shutil, "move", boom)
    out = commit(env, preview(env)["token"])
    assert out["verified"] is True
    assert any("tidy failed" in p for p in out["problems"])


@pytest.mark.parametrize("desc", ["", "   "])
def test_preview_rejects_blank_description(env, desc):
    out = run(env.tools["vault_mcmaster_intake_preview"](
        mcmaster_pn="90510A313", spec_card_json=json.dumps(CARD), description=desc))
    assert out["error"] is True and "description is required" in out["message"]


def test_commit_defaults_to_bridge_for_inventor(monkeypatch, tmp_path):
    """With no inventor injected, the STEP import and running check go to vault_bridge."""
    dl = tmp_path / "Downloads"
    dl.mkdir()
    (dl / "90510A313_Nut.STEP").write_bytes(b"step")
    (dl / "90510A313.pdf").write_bytes(b"%PDF")
    sdk = FakeSDK()
    calls = []
    sdk.check_inventor_running = lambda: calls.append("check") or {"signed_in": True}

    def fake_import(step, out, iprops):
        calls.append("import")
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        Path(out).write_bytes(b"ipt")
        return Path(out)

    sdk.import_step_as_part = fake_import
    monkeypatch.setitem(sys.modules, "vault_bridge", sdk)

    async def fake_fetch(api, vault_id, file_name):
        return {"properties": dict(GOOD_PROPS), "note": ""}

    import check_file_properties
    monkeypatch.setattr(check_file_properties, "fetch_file", fake_fetch)
    mcp = FakeMCP()

    async def no_sleep(_):
        return None

    mi.register(mcp, FakeAPI(), lambda v: "1",
                config={"downloads_dir": str(dl), "workspace_root": str(tmp_path / "ws"), "numbering_scheme": "CD Parts"},
                sleep=no_sleep)
    token = json.loads(asyncio.run(mcp.tools["vault_mcmaster_intake_preview"](
        mcmaster_pn="90510A313", spec_card_json=json.dumps(CARD),
        description="Fully Threaded T-Slot Nut", finish="Black Oxide")))["token"]
    out = json.loads(asyncio.run(mcp.tools["vault_mcmaster_intake_commit"](token=token)))
    assert out["applied"] is True, out
    assert calls == ["check", "import"]
