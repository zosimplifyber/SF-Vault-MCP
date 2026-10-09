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
        self.File = types.SimpleNamespace(ReferencedFileDescriptors=types.SimpleNamespace(Count=0))

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
    monkeypatch.setattr(ia, "get_running_inventor_app", lambda: app)

    def no_launch(visible=True):
        raise AssertionError("import must attach, never launch")

    monkeypatch.setattr(ia, "get_inventor_app", no_launch)
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


def test_reference_model_import_is_rejected_and_closed(tmp_path, inventor):
    doc, _ = inventor
    doc.File.ReferencedFileDescriptors.Count = 1
    step = tmp_path / "a.step"
    step.write_bytes(b"s")
    with pytest.raises(ia.InventorAutomationError, match="Convert Model"):
        ia.import_step_as_part(step, tmp_path / "x.ipt", IPROPS)
    assert doc.closed is True
    assert not (tmp_path / "x.ipt").exists()


class _ComError(Exception):
    pass


def _com(monkeypatch, get_active):
    com = types.SimpleNamespace(CoInitialize=lambda: None, CoUninitialize=lambda: None, com_error=_ComError)
    client = types.SimpleNamespace(
        GetActiveObject=get_active,
        Dispatch=lambda name: (_ for _ in ()).throw(AssertionError("must not launch")))
    monkeypatch.setattr(ia, "_import_win32", lambda: (com, client))


def test_get_running_inventor_app_raises_when_not_running(monkeypatch):
    def nope(name):
        raise _ComError("not running")

    _com(monkeypatch, nope)
    with pytest.raises(ia.InventorAutomationError, match="Inventor is not running"):
        ia.get_running_inventor_app()


def test_get_running_inventor_app_attaches(monkeypatch):
    sentinel = object()
    _com(monkeypatch, lambda name: sentinel)
    assert ia.get_running_inventor_app() is sentinel


def test_check_inventor_running_attaches_and_releases(monkeypatch):
    _com(monkeypatch, lambda name: object())
    ia.check_inventor_running()
    _com(monkeypatch, lambda name: (_ for _ in ()).throw(_ComError("x")))
    with pytest.raises(ia.InventorAutomationError):
        ia.check_inventor_running()
