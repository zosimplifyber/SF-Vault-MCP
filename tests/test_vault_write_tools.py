"""Write tools: previews never touch the SDK; confirm applies grouped writes."""
import asyncio
import json
import sys
import types

import pytest

import vault_write_tools as wt


class FakeMCP:
    def __init__(self):
        self.tools = {}

    def tool(self):
        def deco(fn):
            self.tools[fn.__name__] = fn
            return fn
        return deco


def _lc(state, def_id="7", released=False):
    return {"name": state, "isReleasedState": released, "lifecycleDefinition": {"id": def_id}}


def _fv(name, master, state="Work in Progress", released=False, checked_out_by=None, props=None):
    return {
        "id": f"v{master}", "name": name, "file": {"id": str(master)},
        "lifecycleState": _lc(state, "2", released),
        "isCheckedOut": bool(checked_out_by), "checkoutUserName": checked_out_by,
        "properties": [
            {"value": v, "definition": {"displayName": k, "systemName": k.split(" ")[0], "isSystem": sys_}}
            for k, (v, sys_) in (props or {}).items()
        ],
    }


class FakeAPI:
    def __init__(self, files):
        self.files = files            # name -> fileVersion
        self.jobs = []

    async def search_files(self, vault_id, query, latest_only, limit):
        hits = [fv for n, fv in self.files.items() if query.lower() in n.lower()]
        return {"error": False, "data": {"results": hits}}

    async def get_file_by_id(self, vault_id, file_id):
        for fv in self.files.values():
            if fv["file"]["id"] == str(file_id):
                return {"error": False, "data": {"fileVersion": fv}}
        return {"error": True, "data": "missing"}

    async def get_lifecycle_definition(self, vault_id, definition_id):
        states = [{"id": "27", "name": "Work in Progress"}, {"id": "28", "name": "In Review"},
                  {"id": "29", "name": "Released"}]
        return {"error": False, "data": {"states": states}}

    async def submit_job(self, **kw):
        self.jobs.append(kw)
        return {"error": False, "data": {"id": "j1"}}


class FakeSDK(types.ModuleType):
    def __init__(self):
        super().__init__("vault_sdk")
        self.calls = []

    def update_item_lifecycle_states(self, ids, state_id, comment=""):
        self.calls.append(("items", ids, state_id))

    def update_file_lifecycle_states(self, ids, state_id, comment=""):
        self.calls.append(("files", ids, state_id))

    def update_file_properties(self, ids, props):
        self.calls.append(("props", ids, props))
        return {"files": [{"masterId": i, "id": i + 1000} for i in ids]}

    def undo_checkout_file(self, master_id):
        self.calls.append(("undo", master_id))


ITEMS = {
    "SF-1": {"id": "101", "itemVersion": {"number": "SF-1", "category": "Part - Engineering",
                                          "lifecycleState": _lc("Work in Progress")}},
    "SF-2": {"id": "102", "itemVersion": {"number": "SF-2", "category": "Part - Engineering",
                                          "lifecycleState": _lc("Work in Progress")}},
    "SF-3": {"id": "103", "itemVersion": {"number": "SF-3", "category": "Part - Engineering",
                                          "lifecycleState": _lc("Released")}},
}


async def _resolve_item(vault, pn):
    if pn in ITEMS:
        return {"master": ITEMS[pn], "item_version_id": "x", "item_version": None, "notes": []}
    return {"error": {"message": "nope"}}


@pytest.fixture
def env(monkeypatch):
    sdk = FakeSDK()
    monkeypatch.setitem(sys.modules, "vault_sdk", sdk)
    api = FakeAPI({
        "CD-1.ipt": _fv("CD-1.ipt", 501, props={"Description (File)": ("old", False), "File Name": ("CD-1.ipt", True)}),
        "CD-2.ipt": _fv("CD-2.ipt", 502, state="Released", released=True),
        "LOCK.ipj": _fv("LOCK.ipj", 503, checked_out_by="AYeh"),
    })
    mcp = FakeMCP()
    wt.register(mcp, api, lambda v: "1", _resolve_item)
    return mcp.tools, sdk, api


def _run(coro):
    return json.loads(asyncio.run(coro))


def test_parse_targets_accepts_json_list_scalar_and_commas():
    assert wt.parse_targets('["A", "B", "A"]') == ["A", "B"]
    assert wt.parse_targets("A, B") == ["A", "B"]
    assert wt.parse_targets('"A"') == ["A"]
    assert wt.parse_targets("") == []


def test_item_state_preview_writes_nothing(env):
    tools, sdk, _ = env
    out = _run(tools["vault_change_item_state"]("SF-1, SF-3, SF-9", "In Review"))
    assert out["applied"] is False and "next" in out
    assert [r["status"] for r in out["rows"]] == ["will_change", "will_change", "error"]
    assert sdk.calls == []


def test_item_state_confirm_groups_one_call_per_state(env):
    tools, sdk, _ = env
    out = _run(tools["vault_change_item_state"]('["SF-1", "SF-2"]', "Released", confirm=True))
    assert sdk.calls == [("items", [101, 102], 29)]
    assert out["counts"] == {"changed": 2}


def test_item_state_same_state_is_no_change(env):
    tools, sdk, _ = env
    out = _run(tools["vault_change_item_state"]("SF-3", "released", confirm=True))
    assert out["rows"][0]["status"] == "no_change" and sdk.calls == []


def test_file_state_skips_checked_out_files(env):
    tools, sdk, _ = env
    out = _run(tools["vault_change_file_state"]("LOCK.ipj, CD-1.ipt", "Released", confirm=True))
    statuses = {r["target"]: r["status"] for r in out["rows"]}
    assert statuses == {"LOCK.ipj": "skipped", "CD-1.ipt": "changed"}
    assert sdk.calls == [("files", [501], 29)]


def test_file_properties_validate_names_and_refuse_system_props(env):
    tools, sdk, _ = env
    out = _run(tools["vault_update_file_properties"](json.dumps({
        "CD-1.ipt": {"Description": "new", "File Name": "x"},
    }), confirm=True))
    row = out["rows"][0]
    assert row["status"] == "error" and "system property" in row["reason"]
    assert sdk.calls == []


def test_file_properties_write_then_queue_sync_job(env):
    tools, sdk, api = env
    out = _run(tools["vault_update_file_properties"](json.dumps({
        "CD-1.ipt": {"description": "new"},
        "CD-2.ipt": {"Description": "x"},
    }), confirm=True))
    by = {r["target"]: r for r in out["rows"]}
    assert by["CD-1.ipt"]["status"] == "changed"
    assert by["CD-1.ipt"]["changes"] == {"Description (File)": {"from": "old", "to": "new"}}
    assert by["CD-2.ipt"]["status"] in ("skipped", "error")
    assert sdk.calls == [("props", [501], {"Description (File)": "new"})]
    assert api.jobs[0]["params"] == {"FileVersionId": "1501"}


def test_undo_checkout_preview_warns_then_applies(env):
    tools, sdk, _ = env
    preview = _run(tools["vault_undo_check_out_file"]("LOCK.ipj"))
    assert preview["rows"][0]["status"] == "will_change" and "AYeh" in preview["rows"][0]["warning"]
    assert sdk.calls == []
    done = _run(tools["vault_undo_check_out_file"]("LOCK.ipj", confirm=True))
    assert done["rows"][0]["status"] == "changed" and sdk.calls == [("undo", 503)]


def test_ambiguous_file_name_is_an_error(env):
    tools, _, api = env
    api.files["CD-1.idw"] = _fv("CD-1.idw", 504)
    out = _run(tools["vault_change_file_state"]("CD-1", "Released"))
    assert out["rows"][0]["status"] == "error" and "several files" in out["rows"][0]["reason"]
