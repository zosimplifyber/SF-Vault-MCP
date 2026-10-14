# tests/conftest.py
"""Suite-wide guards.

Sheet generation now asks Vault for each part's lifecycle state. On a developer
machine `config.json` holds live credentials, so any test that calls
`generate_from_file` would quietly hit the network — slow, and failing off-VPN.
Stub the lookup for every test; the tests that care about state override it.
"""
import os
import sys

import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

# The shared Tk bootstrap lives in tests/tk_helpers.py — see its docstring for
# why the naive skip-on-TclError pattern silently hides assertions.


@pytest.fixture(autouse=True)
def _no_live_vault_lookups(monkeypatch):
    # Stub the network boundary, not lookup_file_states itself, so the tests that
    # exercise the lookup's own config/error handling still run the real thing.
    import vault_state

    async def _no_fetch(cfg, numbers):
        return {}

    monkeypatch.setattr(vault_state, "_fetch_states", _no_fetch)


_INVENTOR_PROGIDS = {"inventor.application"}


@pytest.fixture(autouse=True)
def _no_real_inventor(monkeypatch, request):
    """Never start or attach to a real Inventor from the test suite.

    A test that reached win32com with "Inventor.Application" launched a hidden
    /Automation Inventor each run (eight were found on 2026-10-13), holding memory
    and blocking add-in installs. Tests that need COM supply their own fakes.
    """
    try:
        import pythoncom
        import win32com.client as w
    except ImportError:
        return

    def _guard(real):
        def wrapper(progid, *args, **kwargs):
            if str(progid).strip().lower() in _INVENTOR_PROGIDS:
                if os.environ.get("SFVB_GUARD_LOG"):
                    with open(os.environ["SFVB_GUARD_LOG"], "a", encoding="utf-8") as fh:
                        fh.write(request.node.nodeid + "\n")
                raise pythoncom.com_error(
                    -2147221021, f"test suite blocked Inventor COM ({request.node.nodeid})", None, None)
            return real(progid, *args, **kwargs)
        return wrapper

    for name in ("Dispatch", "DispatchEx", "GetActiveObject"):
        if hasattr(w, name):
            monkeypatch.setattr(w, name, _guard(getattr(w, name)))
