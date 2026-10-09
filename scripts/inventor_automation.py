"""
Inventor COM automation helpers. Two callers, two jobs.

**The release workflow** (``scripts/release_workflow.py``) drives a running
(or freshly launched) Inventor instance to:
  * open an assembly from a local working-folder path
  * trigger the Vault add-in to "Get Latest" on the file (so all referenced
    parts are pulled local before the rebuild)
  * rebuild / Update2 the assembly
  * save the assembly (which lets the Vault add-in mark it dirty for check-in)

**The formed fiber handoff tool** (``gui/formed_fiber_handoff.py``) reads a
part's computed mass and volume — ``read_part_physical_properties`` — to fill
the Bone Dry Weight and Part Volume fields on its document.

Why COM and not REST?
---------------------
For the release workflow: the Vault REST v2 API can download file *bytes*
directly, but it cannot restore a working-folder layout (relative paths,
library paths, custom content centre links) the way the Vault add-in does.
The Inventor add-in also handles linked-file resolution. So for any release
that needs the assembly to actually open and rebuild correctly, we drive
Inventor.

For the handoff tool: Vault simply has no such properties. None of its 125
property definitions is Mass, Volume, Density or Thickness, so there is
nothing for a REST call to return. Adding a Vault UDP mapped to Inventor's
Mass was the alternative, and was rejected — it needs a Vault Settings change
plus a re-index before existing files carry it.

A caller on a worker thread
---------------------------
The release workflow calls from the CLI's main thread; the handoff GUI calls
from a worker thread, where COM must be initialised explicitly.
``read_part_physical_properties`` therefore handles its own
``CoInitialize``/``CoUninitialize``. The older entry points do not, and are
main-thread only. A future off-main-thread caller needs the same treatment --
and note it is not a mechanical wrapper: ``get_inventor_app`` returns a live
COM pointer that outlives its own call, so uninitializing the apartment
around it would tear COM down while the caller still holds ``app``. See how
``_read_mass_properties`` is split from its caller to get the ordering right.

Requires `pywin32` (`pip install pywin32`) and a local Inventor install.
The module is import-safe on machines without Inventor — every entry point
raises a clear `InventorUnavailableError` if the COM object cannot be
created, so the workflow can degrade to "open this assembly manually".
"""

from __future__ import annotations

import logging
import time
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Optional

logger = logging.getLogger(__name__)


class InventorUnavailableError(RuntimeError):
    """Raised when Inventor / pywin32 is not available on this machine."""


class InventorAutomationError(RuntimeError):
    """Raised when an Inventor COM operation fails."""


# ---------------------------------------------------------------------------
# COM bootstrap
# ---------------------------------------------------------------------------

def _import_win32():
    try:
        import pythoncom  # noqa: F401
        import win32com.client  # noqa: F401
        return pythoncom, win32com.client
    except ImportError as exc:
        raise InventorUnavailableError(
            "pywin32 is not installed. Install it with: pip install pywin32"
        ) from exc


def get_inventor_app(*, visible: bool = True):
    """Return a live Inventor.Application COM object.

    Tries to attach to a running Inventor first; falls back to launching a
    new instance. Raises ``InventorUnavailableError`` if Inventor isn't
    installed and ``InventorAutomationError`` for any COM failure.
    """
    pythoncom, win32com_client = _import_win32()

    try:
        # Prefer attaching to an already-open Inventor — faster, and avoids
        # spawning a second process that won't share the user's session.
        app = win32com_client.GetActiveObject("Inventor.Application")
        logger.info("Attached to running Inventor.Application")
    except pythoncom.com_error:
        try:
            app = win32com_client.Dispatch("Inventor.Application")
            logger.info("Started new Inventor.Application")
        except pythoncom.com_error as exc:
            raise InventorUnavailableError(
                f"Could not start Inventor.Application: {exc}. "
                "Is Autodesk Inventor installed on this machine?"
            ) from exc

    try:
        app.Visible = visible
    except Exception:  # noqa: BLE001
        # Some Inventor licences forbid hiding the UI; ignore.
        pass

    return app


def get_running_inventor_app():
    """Attach to an already-running Inventor; never launch one.

    A launched Inventor would sign in to Vault on its own and hold the single
    client licence seat the intake commit needs.
    """
    pythoncom, win32com_client = _import_win32()
    try:
        return win32com_client.GetActiveObject("Inventor.Application")
    except pythoncom.com_error as exc:
        raise InventorAutomationError(
            "Inventor is not running. Start Inventor, sign out of its Vault add-in, then retry."
        ) from exc


def check_inventor_running() -> None:
    """Raise InventorAutomationError unless Inventor is running. Safe on a worker thread."""
    pythoncom, _ = _import_win32()
    pythoncom.CoInitialize()
    try:
        app = get_running_inventor_app()
        del app
    finally:
        pythoncom.CoUninitialize()


# ---------------------------------------------------------------------------
# Assembly operations
# ---------------------------------------------------------------------------

@contextmanager
def open_document(
    app,
    file_path: str | Path,
    *,
    save_on_close: bool = False,
    open_visible: bool = True,
) -> Iterator:
    """Open ``file_path`` in Inventor and yield the resulting Document object.

    The document is closed automatically on exit. Pass ``save_on_close=True``
    to commit changes (caller is responsible for triggering `update`/save).

    Pass ``open_visible=False`` for a read-only property pull: the document
    loads without a window, which is faster and leaves whatever the user has
    on screen undisturbed. The default stays True so the release workflow,
    which wants to see what it is rebuilding, is unaffected.
    """
    p = Path(file_path).expanduser().resolve()
    if not p.exists():
        raise InventorAutomationError(f"File does not exist: {p}")

    logger.info("Inventor: opening %s (visible=%s)", p, open_visible)
    try:
        doc = app.Documents.Open(str(p), open_visible)
    except Exception as exc:  # noqa: BLE001
        raise InventorAutomationError(f"Documents.Open failed for {p}: {exc}") from exc

    try:
        yield doc
    finally:
        try:
            logger.info("Inventor: closing %s (save=%s)", p, save_on_close)
            doc.Close(not save_on_close)  # SkipSave = inverse of save_on_close
        except Exception as exc:  # noqa: BLE001
            logger.warning("Document.Close failed: %s", exc)


def rebuild_document(doc) -> None:
    """Force a full rebuild (Update2) on the document.

    Inventor's Update2(True) re-evaluates every feature in the model and is
    the rebuild equivalent of the user pressing "Update" in the ribbon.
    """
    logger.info("Inventor: rebuild (Update2) — %s", _doc_name(doc))
    try:
        doc.Update2(True)
    except AttributeError:
        # Older API — fall back to Update()
        doc.Update()
    except Exception as exc:  # noqa: BLE001
        raise InventorAutomationError(f"Update2 failed: {exc}") from exc


def save_document(doc) -> None:
    """Save the document in place (no SaveAs). The Vault add-in will pick
    this up as a dirty file ready for check-in."""
    logger.info("Inventor: save — %s", _doc_name(doc))
    try:
        doc.Save()
    except Exception as exc:  # noqa: BLE001
        raise InventorAutomationError(f"Document.Save failed: {exc}") from exc


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
        except Exception:  # noqa: BLE001
            ud.Add(value, name)


def import_step_as_part(step_path: str | Path, out_ipt: str | Path, iproperties: dict) -> Path:
    """Open a STEP in Inventor as a part, set iProperties, save as out_ipt.

    Safe on a worker thread (initialises COM itself). Refuses to overwrite an
    existing .ipt, rejects STEPs that open as assemblies, and rejects STEPs
    that open as reference models. Inventor must already be running.
    """
    step, out = Path(step_path), Path(out_ipt)
    if not step.is_file():
        raise InventorAutomationError(f"STEP file not found: {step}")
    if out.exists():
        raise InventorAutomationError(f"Refusing to overwrite existing file {out}")
    pythoncom, _ = _import_win32()
    pythoncom.CoInitialize()
    try:
        app = get_running_inventor_app()
        doc = app.Documents.Open(str(step), False)
        try:
            if doc.DocumentType != PART_DOCUMENT:
                raise InventorAutomationError(
                    f"{step.name} opened as an assembly, not a single part. Import it by hand.")
            if doc.File.ReferencedFileDescriptors.Count > 0:
                raise InventorAutomationError(
                    "The STEP opened as a reference model. Set Inventor's STEP import to Convert Model, then retry.")
            set_iproperties(doc, iproperties.get("design", {}), iproperties.get("custom", {}))
            out.parent.mkdir(parents=True, exist_ok=True)
            doc.SaveAs(str(out), False)
        finally:
            doc.Close(True)
        del doc, app
    finally:
        pythoncom.CoUninitialize()
    return out


def _doc_name(doc) -> str:
    try:
        return str(doc.FullFileName)
    except Exception:  # noqa: BLE001
        return "<document>"


# ---------------------------------------------------------------------------
# Vault add-in commands
# ---------------------------------------------------------------------------

# Internal command IDs registered by the Inventor Vault add-in. These have
# been stable for many Inventor releases; if a particular install renames
# them the call raises and we surface a clear error.
_VAULT_GET_LATEST_CMD = "Connectivity.VaultAddinServer.GetCommand"
_VAULT_CHECK_IN_CMD   = "Connectivity.VaultAddinServer.CheckinCommand"


def _execute_command(app, command_id: str) -> None:
    try:
        cmd = app.CommandManager.ControlDefinitions.Item(command_id)
    except Exception as exc:  # noqa: BLE001
        raise InventorAutomationError(
            f"Vault add-in command not found: {command_id}. "
            "Is the Inventor Vault add-in loaded?"
        ) from exc
    try:
        cmd.Execute()
    except Exception as exc:  # noqa: BLE001
        raise InventorAutomationError(f"Command {command_id} failed: {exc}") from exc


def vault_get_latest(app) -> None:
    """Trigger the Vault add-in's Get Latest on the active document.

    Note: this opens the Vault add-in dialog modally — the workflow will
    block until the user completes it. There is no headless equivalent
    exposed to COM.
    """
    logger.info("Inventor: dispatching Vault add-in 'Get Latest' command")
    _execute_command(app, _VAULT_GET_LATEST_CMD)


def vault_check_in(app) -> None:
    """Trigger the Vault add-in's Check In command for the active document.

    Like ``vault_get_latest`` this is modal — workflow blocks until the
    Vault dialog is dismissed.
    """
    logger.info("Inventor: dispatching Vault add-in 'Check In' command")
    _execute_command(app, _VAULT_CHECK_IN_CMD)


# ---------------------------------------------------------------------------
# Convenience: full rebuild-and-save for a single assembly
# ---------------------------------------------------------------------------

def rebuild_and_save_assembly(
    file_path: str | Path,
    *,
    visible: bool = True,
    settle_seconds: float = 1.0,
) -> str:
    """Open → rebuild → save an assembly. Returns the absolute path used.

    Raises ``InventorUnavailableError`` if Inventor / pywin32 isn't
    available, ``InventorAutomationError`` for any operation failure.
    """
    app = get_inventor_app(visible=visible)
    with open_document(app, file_path, save_on_close=True) as doc:
        # Brief settle so the Vault add-in finishes attaching to the doc
        time.sleep(settle_seconds)
        rebuild_document(doc)
        save_document(doc)
    return str(Path(file_path).expanduser().resolve())


# ---------------------------------------------------------------------------
# Physical properties (mass / volume)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class PhysicalProperties:
    """A part's computed mass and volume, in the units the handoff prints.

    A named result rather than a bare tuple: at the call site, swapping mass
    and volume would otherwise be silent.
    """

    mass_g: float
    volume_cm3: float


def _read_mass_properties(file_path: str | Path) -> tuple[float, float]:
    """``(mass_kg, volume_cm3)`` straight off the model, as plain floats.

    Split out from ``read_part_physical_properties`` for COM lifetime, not
    for tidiness. Every COM pointer this touches -- the Application, the
    Document, the MassProperties object -- is a local of THIS frame, so all
    of them are released when it returns. The caller can then safely call
    ``CoUninitialize``. Holding them in the same frame as the ``finally``
    would release them after the apartment had already been torn down, which
    for an out-of-process server like Inventor can drop the Release, strand
    a refcount in the server, and print `Exception ignored in __del__` noise.
    No test with fake COM objects can catch that -- fakes are plain Python.
    """
    # get_inventor_app's default visible=True is deliberate. Passing False
    # sets Visible on the APPLICATION, and since it prefers attaching to an
    # already-running Inventor, that would hide the user's own window
    # mid-session. The document is opened invisibly instead.
    app = get_inventor_app()
    resolved = Path(file_path).expanduser().resolve()
    with open_document(app, resolved, open_visible=False) as doc:
        try:
            mass_properties = doc.ComponentDefinition.MassProperties
            return float(mass_properties.Mass), float(mass_properties.Volume)
        except Exception as exc:  # noqa: BLE001
            # Wide on purpose, but only three lines wide. COM raises
            # pywintypes.com_error (unimportable at module scope off
            # Windows); an assembly instead of a part raises AttributeError;
            # an unexpected VARIANT raises TypeError from float(). One
            # domain error beats three raw tracebacks.
            raise InventorAutomationError(
                f"Could not read mass properties from {resolved}. Is it a "
                f"part (.ipt) rather than an assembly? ({exc})"
            ) from exc


def read_part_physical_properties(file_path: str | Path) -> PhysicalProperties:
    """Return the part's computed mass in grams and volume in cm³.

    Read from ``MassProperties``, not from the ``Mass`` / ``Volume``
    iProperty strings. The API reports database units -- kilograms and cubic
    centimetres -- regardless of the document's display units, so mass is an
    exact ``* 1000`` and volume needs no conversion at all. The iProperty
    strings are formatted in the document's units and would need parsing.

    Both values come from ONE document open. Opening Inventor is the slowest
    thing the handoff tool does; doing it twice for two properties of the
    same part would double it.

    COM is initialised here rather than by the caller. The handoff GUI reads
    on a worker thread, where an uninitialised apartment fails with an error
    that points nowhere near the real cause. ``scripts/release_workflow.py``
    calls from the main thread, where this is a harmless no-op.

    Note what this does NOT verify: that the mass is the part's bone dry
    weight. That holds only if the assigned material's density is the dried
    fibre density. Inventor substitutes a default material rather than
    failing, so a wrong density yields a plausible wrong number, not an
    error. The handoff form keeps the field editable and labels it as read
    from the model for exactly this reason.

    Raises ``InventorUnavailableError`` (no Inventor, no pywin32) or
    ``InventorAutomationError`` (open failed, not a part document, properties
    unreadable).
    """
    pythoncom, _ = _import_win32()
    pythoncom.CoInitialize()
    try:
        # Every COM pointer lives and dies inside this call, so all of them
        # are released before CoUninitialize runs below. See its docstring.
        mass_kg, volume_cm3 = _read_mass_properties(file_path)
    finally:
        pythoncom.CoUninitialize()
    return PhysicalProperties(mass_g=mass_kg * 1000.0, volume_cm3=volume_cm3)


__all__ = [
    "InventorUnavailableError",
    "InventorAutomationError",
    "PhysicalProperties",
    "get_inventor_app",
    "open_document",
    "rebuild_document",
    "save_document",
    "PART_DOCUMENT",
    "set_iproperties",
    "import_step_as_part",
    "vault_get_latest",
    "vault_check_in",
    "rebuild_and_save_assembly",
    "read_part_physical_properties",
]
