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
