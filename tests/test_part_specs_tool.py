# tests/test_part_specs_tool.py
import asyncio
import json
import sys

import pytest

import mcmaster_intake as mi
from tests.test_mcmaster_intake_tools import FakeMCP, FakeSDK
from tests.test_part_specs import CARD


def fv(name, vid, category="Part - Purchased", folder="$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts"):
    # Real REST shape: category is a plain string, ids are strings, folder under "parent".
    return {"id": str(vid), "name": name, "category": category, "file": {"id": str(vid - 1)},
            "parent": {"fullName": folder}}


class SpecsAPI:
    def __init__(self):
        card = dict(CARD, cd_number="CD-001888")
        self.files = {
            "CD-001887.iam": fv("CD-001887.iam", 100, "Assembly - Engineering", "$/DESIGNS/PRODUCTION EQUIPMENT"),
            "CD-001888.ipt": fv("CD-001888.ipt", 200),
            "CD-001890.iam": fv("CD-001890.iam", 600, "Assembly - Engineering", "$/DESIGNS/PRODUCTION EQUIPMENT"),
            "CD-001890.ipt": fv("CD-001890.ipt", 700),
        }
        self.uses = {
            "100": [("CD-001888.ipt", 200, "Dependency"),
                    ("ISO 4762 - M8 x 30 - Steel.ipt", 300, "Dependency"),
                    ("CD-001888.ipt", 200, "Dependency"),
                    ("CD-001886.ipt", 400, "Dependency"),
                    ("Odd Washer.ipt", 500, "Dependency"),
                    ("CD-001700.ipt", 800, "Dependency")],
            "200": [("CD-001888.specs.json", 210, "Attachment"), ("CD-001888 McMaster 90510A313.pdf", 220, "Attachment")],
            "300": [], "400": [], "500": [], "600": [], "700": [], "800": [],
        }
        self.content = {"210": json.dumps(card).encode()}
        self.cats = {300: ("Part - Content Center", "$/Content Center Files/en-US/ISO 4762"),
                     500: ("Part - Content Center", "$/Content Center Files/en-US/Odd"),
                     800: ("Part - Engineering", "$/DESIGNS/PRODUCTION EQUIPMENT")}

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
            rows.append({"parentFile": {"id": str(file_version_id)}, "fileAssocType": assoc,
                         "childFile": fv(name, vid, cat, folder)})
        return {"error": False, "data": {"results": rows}}

    async def download_file_version_content(self, vault_id, file_version_id, timeout=180.0):
        return {"error": False, "data": self.content[str(file_version_id)]}


@pytest.fixture
def tool(monkeypatch, tmp_path):
    monkeypatch.setitem(sys.modules, "vault_bridge", FakeSDK())
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


def test_in_house_part_is_not_a_purchased_gap(tool):
    out = run(tool(part="CD-001887.iam"))
    gaps = {g["file"]: g["reason"] for g in out["no_data"]}
    assert gaps["CD-001700.ipt"] == "not a purchased or Content Center part"


def test_bare_cd_resolves_ipt(tool):
    out = run(tool(part="cd-001888"))
    assert out["part"] == "CD-001888.ipt"
    assert out["specs"][0]["file"] == "CD-001888.ipt"


def test_bare_cd_prefers_iam(tool):
    out = run(tool(part="CD-001890"))
    assert out["part"] == "CD-001890.iam"


def test_bare_cd_missing(tool):
    out = run(tool(part="CD-999999"))
    assert out["error"] is True
    assert "CD-999999" in json.dumps(out)


def test_truncated_flag_when_max_parts_hit(monkeypatch, tmp_path):
    api = SpecsAPI()
    api.files["CD-001890.iam"] = fv("CD-001890.iam", 600, "Assembly - Engineering", "$/DESIGNS/PRODUCTION EQUIPMENT")
    api.uses["600"] = [("CD-001888.ipt", 200, "Dependency"), ("Sub.iam", 650, "Dependency")]
    api.uses["650"] = [("CD-001886.ipt", 400, "Dependency")]
    monkeypatch.setitem(sys.modules, "vault_bridge", FakeSDK())
    monkeypatch.setattr(mi, "MAX_PARTS", 1)
    mcp = FakeMCP()
    mi.register(mcp, api, lambda v: "1", config={"downloads_dir": str(tmp_path)})
    out = run(mcp.tools["vault_get_part_specs"](part="CD-001890.iam"))
    assert out["truncated"] is True


def test_not_truncated_normally(tool):
    assert run(tool(part="CD-001887.iam"))["truncated"] is False
