import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import vault_bridge as vb  # noqa: E402

TOKEN = "a" * 64


class Handler(BaseHTTPRequestHandler):
    replies = {}
    seen = []
    delay = 0.0

    def do_POST(self):
        import time
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")
        op = self.path.rsplit("/", 1)[-1]
        type(self).seen.append((op, body, self.headers.get(vb.TOKEN_HEADER)))
        if type(self).delay:
            time.sleep(type(self).delay)
        status, payload = type(self).replies.get(op, (200, {}))
        data = json.dumps(payload).encode()
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except ConnectionError:
            pass  # client already gave up (timeout test)

    def log_message(self, *args):
        pass


class QuietServer(ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        pass


class DroppingHandler(BaseHTTPRequestHandler):
    """Reads the request, then closes the socket without replying."""

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)
        self.close_connection = True

    def log_message(self, *args):
        pass


@pytest.fixture
def server(monkeypatch, tmp_path):
    Handler.replies, Handler.seen, Handler.delay = {}, [], 0.0
    httpd = QuietServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    monkeypatch.setattr(vb, "BASE_URL", f"http://127.0.0.1:{httpd.server_port}")
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    yield Handler
    httpd.shutdown()
    httpd.server_close()


class TruncatedErrorHandler(BaseHTTPRequestHandler):
    """Sends a 500 whose Content-Length promises 100 bytes, then closes after 5."""

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)
        self.send_response(500)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", "100")
        self.end_headers()
        self.wfile.write(b'{"err')
        self.close_connection = True

    def log_message(self, *args):
        pass


def test_truncated_error_body_is_uncertain(monkeypatch, tmp_path):
    httpd = QuietServer(("127.0.0.1", 0), TruncatedErrorHandler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    monkeypatch.setattr(vb, "BASE_URL", f"http://127.0.0.1:{httpd.server_port}")
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    try:
        with pytest.raises(vb.BridgeError) as info:
            vb.add_file("$/X", r"C:\Vault Workspace\x.pdf", "x.pdf")
        assert info.value.code == "connection_lost"
    finally:
        httpd.shutdown()
        httpd.server_close()


def test_dropped_connection_after_send_is_uncertain(monkeypatch, tmp_path):
    httpd = QuietServer(("127.0.0.1", 0), DroppingHandler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    monkeypatch.setattr(vb, "BASE_URL", f"http://127.0.0.1:{httpd.server_port}")
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    try:
        with pytest.raises(vb.BridgeError) as info:
            vb.add_file("$/X", r"C:\Vault Workspace\x.pdf", "x.pdf")
        assert info.value.code == "connection_lost"
        assert "check Vault" in str(info.value)
    finally:
        httpd.shutdown()
        httpd.server_close()


def test_sends_token_and_body(server):
    server.replies["add_file"] = (200, {"id": 510, "masterId": 509, "name": "x.pdf", "checkedOut": False})
    out = vb.add_file("$/X", r"C:\Vault Workspace\x.pdf", "x.pdf", attachment_file_ids=["7"], comment="c")
    assert out["masterId"] == 509
    op, body, token = server.seen[0]
    assert (op, token) == ("add_file", TOKEN)
    assert body == {"folderPath": "$/X", "localPath": r"C:\Vault Workspace\x.pdf", "fileName": "x.pdf",
                    "attachmentFileIds": [7], "comment": "c"}


def test_call_shapes(server):
    vb.generate_file_number("12")
    vb.update_file_categories(["5", 6], "30", comment="intake")
    vb.import_step_as_part(r"C:\a.STEP", r"C:\b.ipt", {"design": {"Part Number": "CD-1"}, "custom": {}})
    vb.get_folder_by_path("$/X")
    vb.add_folder("$/X", "nuts")
    assert [(o, b) for o, b, _ in server.seen] == [
        ("generate_file_number", {"schemeId": 12}),
        ("update_file_categories", {"masterIds": [5, 6], "categoryId": 30, "comment": "intake"}),
        ("import_step_as_part", {"stepPath": r"C:\a.STEP", "outIpt": r"C:\b.ipt",
                                 "iproperties": {"design": {"Part Number": "CD-1"}, "custom": {}}}),
        ("get_folder_by_path", {"path": "$/X"}),
        ("add_folder", {"parentPath": "$/X", "name": "nuts"}),
    ]


def test_update_categories_rejects_empty(server):
    with pytest.raises(ValueError):
        vb.update_file_categories([], 30)
    assert server.seen == []


def test_not_signed_in_is_unavailable(server):
    server.replies["generate_file_number"] = (400, {"error": "not_signed_in", "message": "Sign in to Vault in Inventor (Vault tab, Log In)."})
    with pytest.raises(vb.BridgeUnavailable, match="Sign in to Vault"):
        vb.generate_file_number(12)


def test_other_error_keeps_code(server):
    server.replies["add_file"] = (400, {"error": "path_not_allowed", "message": "nope"})
    with pytest.raises(vb.BridgeError) as info:
        vb.add_file("$/X", r"C:\Windows\x", "x")
    assert info.value.code == "path_not_allowed"


def test_check_inventor_running(server):
    server.replies["status"] = (200, {"signed_in": False})
    with pytest.raises(vb.BridgeUnavailable, match="Sign in to Vault"):
        vb.check_inventor_running()
    server.replies["status"] = (200, {"signed_in": True, "user": "zak"})
    assert vb.check_inventor_running()["user"] == "zak"


def test_no_server_is_unavailable(monkeypatch, tmp_path):
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    monkeypatch.setattr(vb, "BASE_URL", "http://127.0.0.1:9")
    with pytest.raises(vb.BridgeUnavailable, match="Inventor is not running"):
        vb.status()


def test_no_token_file_is_unavailable(monkeypatch, tmp_path):
    monkeypatch.setattr(vb, "token_path", lambda: tmp_path / "missing")
    with pytest.raises(vb.BridgeUnavailable, match="never started"):
        vb.status()


def test_timeout_is_uncertain_not_unavailable(server):
    server.delay = 1.0
    with pytest.raises(vb.BridgeError) as info:
        vb._call("status", {}, timeout=0.2)
    assert info.value.code == "timeout"


def test_fix_data_card_request_body(server):
    server.replies["fix_data_card"] = (200, {"results": [], "counts": {"preview": 0, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    vb.fix_data_card(["CD-001885.ipt"])
    vb.fix_data_card(("CD-001897.ipt", "$/A/B.ipt"), design={"Vendor": "X"}, custom={"Source": "Buy"},
                     part_number_from_file_name=False, dry_run=False, comment="c")
    assert [(o, b) for o, b, _ in server.seen] == [
        ("fix_data_card", {"files": ["CD-001885.ipt"], "partNumberFromFileName": True, "dryRun": True}),
        ("fix_data_card", {"files": ["CD-001897.ipt", "$/A/B.ipt"], "partNumberFromFileName": False, "dryRun": False,
                           "design": {"Vendor": "X"}, "custom": {"Source": "Buy"}, "comment": "c"}),
    ]


def test_fix_data_card_rejects_empty(server):
    with pytest.raises(ValueError):
        vb.fix_data_card([])
    assert server.seen == []


def test_fix_data_card_cli_parsing(server, capsys):
    server.replies["fix_data_card"] = (200, {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    assert vb.main(["fix-data-card", "CD-001885.ipt", "CD-001897.ipt"]) == 0
    assert vb.main(["fix-data-card", "CD-001885.ipt", "--set", "Vendor=McMASTER-CARR", "--custom", "Source=Buy=x",
                    "--no-part-number", "--apply", "--comment", "Data card fix"]) == 0
    assert server.seen[0][1] == {"files": ["CD-001885.ipt", "CD-001897.ipt"], "partNumberFromFileName": True, "dryRun": True}
    assert server.seen[1][1] == {"files": ["CD-001885.ipt"], "partNumberFromFileName": False, "dryRun": False,
                                 "design": {"Vendor": "McMASTER-CARR"}, "custom": {"Source": "Buy=x"}, "comment": "Data card fix"}
    assert '"counts"' in capsys.readouterr().out


def test_fix_data_card_cli_rejects_bad_pair():
    with pytest.raises(SystemExit):
        vb.main(["fix-data-card", "a.ipt", "--set", "novalue"])


def test_fix_data_card_revise_released_flag(server):
    server.replies["fix_data_card"] = (200, {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    vb.fix_data_card(["a.ipt"], design={"Cost": "12.5"})
    vb.fix_data_card(["a.ipt"], revise_released=True)
    assert vb.main(["fix-data-card", "a.ipt", "--set", "Cost=1.25", "--revise-released"]) == 0
    assert "reviseReleased" not in server.seen[0][1]
    assert server.seen[0][1]["design"] == {"Cost": "12.5"}
    assert server.seen[1][1]["reviseReleased"] is True
    assert server.seen[2][1]["reviseReleased"] is True and server.seen[2][1]["dryRun"] is True


def test_fix_data_card_summary_title(server):
    server.replies["fix_data_card"] = (200, {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    vb.fix_data_card(["a.ipt"])
    vb.fix_data_card(["a.ipt"], summary={"Title": "CD-1-Washer"})
    assert vb.main(["fix-data-card", "a.ipt", "--summary", "Title=CD-1-Steel=Washer"]) == 0
    assert "summary" not in server.seen[0][1]
    assert server.seen[1][1]["summary"] == {"Title": "CD-1-Washer"}
    assert server.seen[2][1]["summary"] == {"Title": "CD-1-Steel=Washer"}


def test_attach_vendor_pdf_request_body(server):
    server.replies["attach_vendor_pdf"] = (200, {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    vb.attach_vendor_pdf(["CD-001897.ipt"])
    vb.attach_vendor_pdf(("CD-001897.ipt", "CD-001885.ipt"), dry_run=False, revise_released=True, comment="Attach PDF")
    assert server.seen[0][0] == "attach_vendor_pdf"
    assert server.seen[0][1] == {"files": ["CD-001897.ipt"], "source": "mcmaster", "dryRun": True}
    assert server.seen[1][1] == {"files": ["CD-001897.ipt", "CD-001885.ipt"], "source": "mcmaster", "dryRun": False,
                                 "reviseReleased": True, "comment": "Attach PDF"}


def test_attach_vendor_pdf_rejects_empty(server):
    with pytest.raises(ValueError):
        vb.attach_vendor_pdf([])


def test_attach_vendor_pdf_cli(server, capsys):
    server.replies["attach_vendor_pdf"] = (200, {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}})
    assert vb.main(["attach-vendor-pdf", "CD-001897.ipt", "CD-001885.ipt"]) == 0
    assert vb.main(["attach-vendor-pdf", "CD-001162.ipt", "--source", r"C:\Users\z\Downloads\x.pdf", "--apply", "--revise-released"]) == 0
    assert server.seen[0][1] == {"files": ["CD-001897.ipt", "CD-001885.ipt"], "source": "mcmaster", "dryRun": True}
    assert server.seen[1][1] == {"files": ["CD-001162.ipt"], "source": r"C:\Users\z\Downloads\x.pdf", "dryRun": False, "reviseReleased": True}
    assert '"counts"' in capsys.readouterr().out


def test_attach_vendor_pdf_cli_exit_code_follows_errors(server):
    server.replies["attach_vendor_pdf"] = (200, {"results": [], "counts": {"preview": 0, "fixed": 0, "no_change": 0, "skipped": 0, "error": 1}})
    assert vb.main(["attach-vendor-pdf", "a.ipt", "--apply"]) == 1


NYLON_JSON = Path(__file__).resolve().parents[1] / "scripts" / "materials" / "nylon12_pa12_mjf.json"
SF_LIBRARY = "$/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib"
_MAT_REPLY = {"results": [], "counts": {"preview": 1, "fixed": 0, "no_change": 0, "skipped": 0, "error": 0}}


def test_sample_material_json_has_the_nylon_values():
    values = json.loads(NYLON_JSON.read_text(encoding="utf-8"))
    assert values["name"] == "Nylon 12 (PA12, MJF)"
    assert values["description"] == "HP MJF PA12, printed by ABC Corp"
    assert values["density"] == {"value": 1.01, "unit": "g/cm3"}
    assert values["youngs_modulus"] == {"value": 1800, "unit": "MPa"}
    assert values["poissons_ratio"]["value"] == 0.40
    assert values["yield_strength"] == values["tensile_strength"] == {"value": 48, "unit": "MPa"}
    assert values["elongation"] == {"value": 20, "unit": "%"}
    assert values["thermal_expansion"] == {"value": 1.0e-4, "unit": "1/C"}
    assert values["thermal_conductivity"] == {"value": 0.2, "unit": "W/(m K)"}
    assert values["material_class"] == "Plastic"


def test_add_library_material_request_body(server):
    server.replies["add_library_material"] = (200, _MAT_REPLY)
    values = {"name": "N", "density": {"value": 1, "unit": "g/cm3"}}
    vb.add_library_material(SF_LIBRARY, values)
    vb.add_library_material(SF_LIBRARY, values, name="Other", dry_run=False, revise_released=True, comment="c")
    assert server.seen[0][0] == "add_library_material"
    assert server.seen[0][1] == {"library": SF_LIBRARY, "values": values, "dryRun": True}
    assert server.seen[1][1] == {"library": SF_LIBRARY, "values": values, "dryRun": False, "name": "Other", "reviseReleased": True, "comment": "c"}


def test_add_library_material_rejects_empty_input(server):
    with pytest.raises(ValueError):
        vb.add_library_material("", {"name": "x"})
    with pytest.raises(ValueError):
        vb.add_library_material(SF_LIBRARY, {})


def test_add_material_cli_previews_then_applies(server, capsys):
    server.replies["add_library_material"] = (200, _MAT_REPLY)
    assert vb.main(["add-material", "--library", SF_LIBRARY, "--name", "Nylon 12 (PA12, MJF)", "--json", str(NYLON_JSON)]) == 0
    assert vb.main(["add-material", "--library", SF_LIBRARY, "--json", str(NYLON_JSON), "--apply", "--comment", "Add PA12"]) == 0
    assert server.seen[0][1]["dryRun"] is True
    assert server.seen[0][1]["name"] == "Nylon 12 (PA12, MJF)"
    assert server.seen[0][1]["values"]["density"] == {"value": 1.01, "unit": "g/cm3"}
    assert server.seen[1][1]["dryRun"] is False
    assert server.seen[1][1]["comment"] == "Add PA12"
    assert "name" not in server.seen[1][1]
    assert '"counts"' in capsys.readouterr().out


def test_add_material_cli_exit_code_follows_errors(server):
    server.replies["add_library_material"] = (200, {"results": [], "counts": {"preview": 0, "fixed": 0, "no_change": 0, "skipped": 0, "error": 1}})
    assert vb.main(["add-material", "--library", SF_LIBRARY, "--json", str(NYLON_JSON), "--apply"]) == 1


def test_list_material_properties_cli(server, capsys):
    server.replies["list_material_properties"] = (200, {"material": "Nylon 12", "count": 0, "rows": []})
    assert vb.main(["list-material-properties", "Nylon 12", "--library", "SF Materials"]) == 0
    vb.list_material_properties("ABS")
    assert server.seen[0][0] == "list_material_properties"
    assert server.seen[0][1] == {"material": "Nylon 12", "library": "SF Materials"}
    assert server.seen[1][1] == {"material": "ABS"}
    with pytest.raises(ValueError):
        vb.list_material_properties(" ")
    assert '"rows"' in capsys.readouterr().out
