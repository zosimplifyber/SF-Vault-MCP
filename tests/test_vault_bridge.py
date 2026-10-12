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
