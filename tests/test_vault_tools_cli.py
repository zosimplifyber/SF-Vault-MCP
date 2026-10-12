import json
import shutil
from pathlib import Path

import vault_tools_cli as cli

FIXTURE = Path(__file__).parent / "fixtures" / "CD-001608-inventor-bom.txt"


def test_purchasing_writes_named_workbook_and_result(tmp_path):
    bom = tmp_path / "bom.txt"
    shutil.copy(FIXTURE, bom)
    result_path = tmp_path / "result.json"
    code = cli.main(["purchasing", "--bom", str(bom), "--assembly", "CD-001608",
                     "--out", str(tmp_path), "--name", "CD-001608 Purchasing.xlsx",
                     "--result", str(result_path)])
    result = json.loads(result_path.read_text(encoding="utf-8"))
    assert code == 0, result
    assert result["ok"] is True
    assert Path(result["workbook"]) == tmp_path / "CD-001608 Purchasing.xlsx"
    assert Path(result["workbook"]).is_file()
    assert not (tmp_path / "CD-001608-PurchasingExport.xlsx").exists()
    assert result["lines"] > 0
    assert isinstance(result["unmatched"], list) and isinstance(result["warnings"], list)


def test_purchasing_replaces_existing_workbook(tmp_path):
    bom = tmp_path / "bom.txt"
    shutil.copy(FIXTURE, bom)
    (tmp_path / "A Purchasing.xlsx").write_bytes(b"old")
    cli.main(["purchasing", "--bom", str(bom), "--assembly", "A", "--out", str(tmp_path),
              "--name", "A Purchasing.xlsx", "--result", str(tmp_path / "r.json")])
    assert (tmp_path / "A Purchasing.xlsx").read_bytes()[:2] == b"PK"


def test_purchasing_reads_quoted_description(tmp_path):
    bom = tmp_path / "bom.txt"
    bom.write_text('Item\tPart Number\tBOM Structure\tUnit QTY\tQTY\tDescription\tREV\tMaterial\tFile Name\r\n'
                   '1\tCD-000001\tPurchased\tEach\t4\t"1/2"" bolt"\t\tSteel\tCD-000001.ipt\r\n', encoding="utf-8")
    out = cli.run_purchasing(str(bom), "X", str(tmp_path), "X Purchasing.xlsx")
    assert out["ok"] is True, out
    assert out["lines"] == 1


def test_failure_is_reported_in_result_file(tmp_path):
    result_path = tmp_path / "r.json"
    code = cli.main(["purchasing", "--bom", str(tmp_path / "missing.txt"), "--assembly", "X",
                     "--out", str(tmp_path), "--name", "X Purchasing.xlsx", "--result", str(result_path)])
    result = json.loads(result_path.read_text(encoding="utf-8"))
    assert code == 1
    assert result["ok"] is False and result["error"]


def test_unexpected_exception_still_writes_result(tmp_path, monkeypatch):
    monkeypatch.setattr(cli, "run_purchasing", lambda *a: (_ for _ in ()).throw(RuntimeError("boom")))
    result_path = tmp_path / "r.json"
    code = cli.main(["purchasing", "--bom", "x", "--assembly", "X", "--out", str(tmp_path),
                     "--name", "n.xlsx", "--result", str(result_path)])
    assert code == 1
    assert "boom" in json.loads(result_path.read_text(encoding="utf-8"))["error"]
