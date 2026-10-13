"""scripts/migrate_list_prices.py with fakes only: no list, Vault or bridge is touched."""
import csv
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import migrate_list_prices as m  # noqa: E402


class BridgeUnavailable(RuntimeError):
    pass


FILES = {
    "CD-1": {"name": "CD-1.ipt", "path": "$/D/CD-1.ipt", "state": "Work in Progress", "category": "Part - Engineering"},
    "CD-2": {"name": "CD-2.ipt", "path": "$/D/CD-2.ipt", "state": "Released", "category": "Part - Engineering"},
    "CD-3": {"name": "CD-3.ipt", "path": "$/Content Center Files/CD-3.ipt", "state": "Released", "category": "Part - Content Center"},
    "CD-4": {"name": "CD-4.ipt", "path": "$/D/CD-4.ipt", "state": "Work in Progress", "category": "Part - Engineering"},
    "CD-5": {"name": "CD-5.iam", "path": "$/D/CD-5.iam", "state": "Work in Progress", "category": "Assembly - Engineering"},
}
CURRENT = {"$/D/CD-1.ipt": "1.00", "$/D/CD-2.ipt": "5.00", "$/D/CD-4.ipt": "2.50", "$/D/CD-5.iam": None}


def lookup(number):
    return FILES.get(number)


LIST_ROWS = [
    {"Number": "CD-1", "Cost Per": 3.5},
    {"Number": "CD-2", "Cost Per": "$7"},
    {"Number": "CD-3", "Cost Per": 9},
    {"Number": "CD-4", "Cost Per": 2.5},
    {"Number": "CD-5", "Cost Per": 100},
    {"Number": "CD-6", "Cost Per": 4},
    {"Number": "CD-7", "Cost Per": None},
    {"Number": "CD-8", "Cost Per": 0},
    {"Number": "CD-1", "Cost Per": 99},
]


class FakeBridge:
    def __init__(self, fail_on_call=None, drop_session_at=None):
        self.calls = []
        self.fail_on_call = fail_on_call
        self.drop_session_at = drop_session_at

    def __call__(self, files, *, dry_run, revise_released=False, cost=""):
        self.calls.append({"files": list(files), "dry_run": dry_run, "revise": revise_released, "cost": cost})
        if not dry_run and self.drop_session_at == len(self.calls):
            raise BridgeUnavailable("Vault sign-in lost")
        results = []
        for f in files:
            cur = CURRENT.get(f)
            if dry_run and f == "$/D/CD-2.ipt" and not revise_released:
                results.append({"file": f, "status": "skipped", "reason": "is Released"})
            elif cur == cost:
                results.append({"file": f, "status": "no_change", "changes": {}})
            else:
                key = "preview" if dry_run else "fixed"
                results.append({"file": f, "status": key, "version": None if dry_run else 4,
                                "changes": {"Cost": {"from": cur, "to": cost}}})
        return {"results": results}


def test_parse_and_format_price():
    assert m.parse_price("$1,234.5") == 1234.5
    assert m.parse_price(0) is None and m.parse_price("n/a") is None and m.parse_price(None) is None
    assert m.parse_price(float("nan")) is None
    assert m.format_price(4.5) == "4.50"
    assert m.format_price(0.125) == "0.125"
    assert m.format_price(3.1 + 0.2) == "3.30"


def test_actions_for_list_rows():
    plan = m.plan_rows(LIST_ROWS, lookup)
    m.fill_current_costs(plan, FakeBridge())
    actions = {r.number: r.action for r in plan}
    assert actions == {
        "CD-1": "update",           # 1.00 -> 3.50
        "CD-2": "released-revise",  # Released, bridge cannot preview it without the flag, flag preview shows 5.00
        "CD-3": "content center",
        "CD-4": "same",             # already 2.50
        "CD-5": "update",           # assembly with no cost yet
        "CD-6": "no file",
        "CD-7": "no price",
        "CD-8": "no price",
    }
    by = {r.number: r for r in plan}
    assert by["CD-1"].current == "1.00" and by["CD-1"].new_price() == "3.50"
    assert by["CD-2"].current == "5.00"
    assert by["CD-6"].price == 4.0 and by["CD-7"].price is None


def test_duplicate_numbers_keep_first_and_only_filters():
    plan = m.plan_rows(LIST_ROWS, lookup, only=["cd-1", "CD-4"])
    assert [r.number for r in plan] == ["CD-1", "CD-4"]
    assert plan[0].price == 3.5


def test_no_price_rows_are_never_looked_up():
    seen = []
    m.plan_rows([{"Number": "X", "Cost Per": 0}], lambda n: seen.append(n))
    assert seen == []


def test_batch_splitting():
    assert list(m.chunks(list(range(5)), 2)) == [[0, 1], [2, 3], [4]]
    assert list(m.chunks([], 3)) == []
    with pytest.raises(ValueError):
        list(m.chunks([1], 0))


def test_apply_batches_and_flags():
    rows = [{"Number": f"CD-{i}", "Cost Per": 1.5} for i in range(1, 6)]
    files = {f"CD-{i}": {"name": f"CD-{i}.ipt", "path": f"$/D/CD-{i}.ipt", "state": "WIP", "category": "Part"}
             for i in range(1, 6)}
    plan = m.plan_rows(rows, files.get)
    fake = FakeBridge()
    results = m.apply_updates(plan, fake, batch=2)
    assert [len(c["files"]) for c in fake.calls] == [2, 2, 1]
    assert all(c["cost"] == "1.50" and not c["dry_run"] and not c["revise"] for c in fake.calls)
    assert len(results) == 5 and {r["status"] for r in results} == {"fixed"}


def test_released_only_with_the_flag():
    plan = m.plan_rows(LIST_ROWS, lookup)
    m.fill_current_costs(plan, FakeBridge())
    plain = FakeBridge()
    m.apply_updates(plan, plain, batch=20)
    sent = [f for c in plain.calls for f in c["files"]]
    assert "$/D/CD-2.ipt" not in sent and not any(c["revise"] for c in plain.calls)
    flagged = FakeBridge()
    m.apply_updates(plan, flagged, batch=20, include_released=True)
    assert "$/D/CD-2.ipt" in [f for c in flagged.calls for f in c["files"]]
    assert all(c["revise"] for c in flagged.calls)
    assert "$/Content Center Files/CD-3.ipt" not in [f for c in flagged.calls for f in c["files"]]


def test_stops_on_the_first_sign_in_loss():
    rows = [{"Number": f"CD-{i}", "Cost Per": float(i) + 10} for i in range(1, 5)]
    files = {f"CD-{i}": {"name": f"CD-{i}.ipt", "path": f"$/D/CD-{i}.ipt", "state": "WIP", "category": "Part"}
             for i in range(1, 5)}
    plan = m.plan_rows(rows, files.get)
    fake = FakeBridge(drop_session_at=2)
    results = m.apply_updates(plan, fake, batch=1)
    assert len(fake.calls) == 2  # the third and fourth batches were never sent
    assert results[0]["status"] == "fixed"
    assert results[1]["status"] == "error" and "not_signed_in" in results[1]["detail"]


def test_sign_in_step_in_a_reply_also_stops():
    plan = m.plan_rows([{"Number": "CD-1", "Cost Per": 2}, {"Number": "CD-4", "Cost Per": 3}], lookup)

    calls = []

    def fix(files, **kw):
        calls.append(files)
        return {"results": [{"file": f, "status": "error", "step": "sign_in", "message": "dropped"} for f in files]}

    results = m.apply_updates(plan, fix, batch=1)
    assert len(calls) == 1 and results[0]["status"] == "error"


def test_main_report_and_apply(tmp_path):
    fake = FakeBridge()
    out = tmp_path / "r.csv"
    code = m.main(["--report", str(out)], list_loader=lambda: LIST_ROWS, lookup_factory=lambda: lookup, fix=fake)
    assert code == 0
    assert all(c["dry_run"] for c in fake.calls)
    rows = list(csv.DictReader(out.open(encoding="utf-8-sig")))
    assert rows[0].keys() == set(m.REPORT_COLUMNS)
    assert {r["Number"]: r["action"] for r in rows}["CD-4"] == "same"

    fake2 = FakeBridge()
    res = tmp_path / "res.csv"
    code = m.main(["--apply", "--only", "CD-1", "--results", str(res)],
                  list_loader=lambda: LIST_ROWS, lookup_factory=lambda: lookup, fix=fake2)
    assert code == 0
    writes = [c for c in fake2.calls if not c["dry_run"]]
    assert writes == [{"files": ["$/D/CD-1.ipt"], "dry_run": False, "revise": False, "cost": "3.50"}]
    assert list(csv.DictReader(res.open(encoding="utf-8-sig")))[0]["status"] == "fixed"


def test_report_and_apply_together_is_refused(tmp_path):
    assert m.main(["--report", "x.csv", "--apply"], list_loader=lambda: [], lookup_factory=lambda: lookup) == 2


# ---- fix round 1 ----

def test_lookup_error_is_not_no_file_and_fatal_stops():
    def flaky(number):
        if number == "CD-2":
            raise m.VaultLookupError("Vault search for CD-2.ipt failed: 500")
        if number == "CD-3":
            raise m.VaultLookupError("Vault search for CD-3.ipt failed: 401 session expired", fatal=True)
        return FILES.get(number)

    rows = [{"Number": f"CD-{i}", "Cost Per": 5} for i in (1, 2, 3, 4)]
    with pytest.raises(m.RunStopped) as stopped:
        m.plan_rows(rows, flaky)
    plan = stopped.value.plan
    assert [r.number for r in plan] == ["CD-1", "CD-2", "CD-3"]  # CD-4 never looked up
    assert plan[1].action == "lookup error" and "500" in plan[1].detail
    assert plan[2].action == "lookup error"


def test_auth_failure_detection():
    assert m.is_auth_failure("File search failed: 401 Unauthorized")
    assert m.is_auth_failure("session expired")
    assert not m.is_auth_failure("File search failed: 500 server error")


def test_pick_latest_takes_highest_version_not_first():
    recs = [{"name": "CD-1.ipt", "version": 2}, {"name": "cd-1.IPT", "version": 7},
            {"name": "CD-10.ipt", "version": 99}, {"name": "CD-1.ipt", "version": 3}]
    assert m.pick_latest(recs, "CD-1.ipt")["version"] == 7
    assert m.pick_latest(recs, "CD-9.ipt") is None
    assert m.pick_latest([{"name": "A.ipt"}, {"name": "A.ipt"}], "A.ipt") is not None


def test_dry_run_error_result_surfaces_as_error_action():
    plan = m.plan_rows([{"Number": "CD-1", "Cost Per": 3.5}, {"Number": "CD-4", "Cost Per": 9}], lookup)

    def fix(files, **kw):
        return {"results": [
            {"file": "$/D/CD-1.ipt", "status": "error", "step": "read", "message": "cannot open"},
            {"file": "$/D/CD-4.ipt", "status": "preview", "changes": {"Cost": {"from": "2.50", "to": "9.00"}}}]}
    m.fill_current_costs(plan, fix)
    assert plan[0].action == "error" and "cannot open" in plan[0].detail
    assert plan[1].action == "update"
    # and apply never queues the errored row
    fake = FakeBridge()
    m.apply_updates(plan, fake)
    assert [f for c in fake.calls for f in c["files"]] == ["$/D/CD-4.ipt"]


def test_dry_run_sign_in_error_stops_the_run(tmp_path):
    calls = []

    def fix(files, **kw):
        calls.append(files)
        return {"results": [{"file": f, "status": "error", "step": "sign_in", "message": "dropped"} for f in files]}
    out = tmp_path / "r.csv"
    code = m.main(["--report", str(out), "--batch", "1"], list_loader=lambda: LIST_ROWS,
                  lookup_factory=lambda: lookup, fix=fix)
    assert code == 1 and len(calls) == 1
    rows = list(csv.DictReader(out.open(encoding="utf-8-sig")))
    assert any(r["action"] == "error" and "sign_in" in r["detail"] for r in rows)


def test_dry_run_bridge_unavailable_stops():
    plan = m.plan_rows([{"Number": "CD-1", "Cost Per": 3.5}], lookup)

    def fix(files, **kw):
        raise BridgeUnavailable("not running")
    with pytest.raises(m.RunStopped) as stopped:
        m.fill_current_costs(plan, fix)
    assert stopped.value.plan[0].action == "error"


def test_apply_stops_after_a_timeout_that_may_have_landed():
    class Timeout(RuntimeError):
        code = "timeout"
    rows = [{"Number": f"CD-{i}", "Cost Per": 20 + i} for i in range(1, 4)]
    files = {f"CD-{i}": {"name": f"CD-{i}.ipt", "path": f"$/D/CD-{i}.ipt", "state": "WIP", "category": "P"}
             for i in range(1, 4)}
    plan = m.plan_rows(rows, files.get)
    calls = []

    def fix(files_, **kw):
        calls.append(files_)
        raise Timeout("slow")
    m.apply_updates(plan, fix, batch=1)
    assert len(calls) == 1


def _released_plan(n=3):
    rows = [{"Number": f"CD-{i}", "Cost Per": 20 + i} for i in range(1, n + 1)]
    files = {f"CD-{i}": {"name": f"CD-{i}.ipt", "path": f"$/D/CD-{i}.ipt", "state": "Released", "category": "P"}
             for i in range(1, n + 1)}
    return m.plan_rows(rows, files.get)


def test_apply_stops_everything_after_a_failed_re_release(capsys):
    plan = _released_plan()
    for r in plan:
        r.action = "released-revise"
    calls = []

    def fix(files_, **kw):
        calls.append(files_)
        return {"results": [{"file": f, "status": "error", "step": "re_release", "message": "no permission"}
                            for f in files_]}
    results = m.apply_updates(plan, fix, batch=1, include_released=True)
    assert len(calls) == 1 and len(results) == 1
    err = capsys.readouterr().err
    assert "CD-1.ipt" in err and "not attempted" in err


def test_apply_stops_on_any_error_while_revising(capsys):
    plan = _released_plan()
    for r in plan:
        r.action = "released-revise"
    calls = []

    def fix(files_, **kw):
        calls.append(files_)
        return {"results": [{"file": f, "status": "error", "step": "edit", "message": "boom"} for f in files_]}
    m.apply_updates(plan, fix, batch=1, include_released=True)
    assert len(calls) == 1


def test_dry_run_skipped_becomes_a_skipped_action_with_reason():
    plan = m.plan_rows([{"Number": "CD-5", "Cost Per": 100}, {"Number": "CD-4", "Cost Per": 9}], lookup)

    def fix(files, **kw):
        return {"results": [
            {"file": "$/D/CD-5.iam", "status": "skipped", "reason": "assemblies not supported yet"},
            {"file": "$/D/CD-4.ipt", "status": "preview", "changes": {"Cost": {"from": "2.50", "to": "9.00"}}}]}
    m.fill_current_costs(plan, fix)
    assert plan[0].action == "skipped" and plan[0].detail == "assemblies not supported yet"
    assert plan[1].action == "update"
    fake = FakeBridge()
    m.apply_updates(plan, fake)
    assert [f for c in fake.calls for f in c["files"]] == ["$/D/CD-4.ipt"]


def test_exact_name_in_two_folders_is_ambiguous():
    recs = [{"name": "CD-1.ipt", "version": 2, "folder": {"fullName": "$/A"}},
            {"name": "CD-1.ipt", "version": 7, "folder": {"fullName": "$/B"}}]
    with pytest.raises(m.AmbiguousMatch) as exc:
        m.pick_latest(recs, "CD-1.ipt")
    assert exc.value.folders == ["$/A", "$/B"]
    same = [{"name": "CD-1.ipt", "version": 2, "folder": {"fullName": "$/A"}},
            {"name": "CD-1.ipt", "version": 7, "folder": {"fullName": "$/A"}}]
    assert m.pick_latest(same, "CD-1.ipt")["version"] == 7


def test_plan_rows_marks_ambiguous():
    def amb(number):
        raise m.AmbiguousMatch(number, ["$/A", "$/B"])
    plan = m.plan_rows([{"Number": "CD-1", "Cost Per": 3}], amb)
    assert plan[0].action == "ambiguous" and "$/A" in plan[0].detail and "$/B" in plan[0].detail
