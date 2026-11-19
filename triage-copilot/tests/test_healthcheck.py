import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from app.healthcheck import check


def serve(status):
    class H(BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(status)
            self.end_headers()

        def log_message(self, *a):
            pass

    srv = HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def run(marker, port):
    return check(str(marker), f"http://127.0.0.1:{port}/health")


def test_both_present_is_healthy(tmp_path):
    srv = serve(200)
    (tmp_path / "m").write_text("1")
    try:
        assert run(tmp_path / "m", srv.server_port) is True
    finally:
        srv.shutdown()


def test_no_marker_is_unhealthy(tmp_path):
    srv = serve(200)
    try:
        assert run(tmp_path / "m", srv.server_port) is False
    finally:
        srv.shutdown()


def test_health_500_is_unhealthy(tmp_path):
    srv = serve(500)
    (tmp_path / "m").write_text("1")
    try:
        assert run(tmp_path / "m", srv.server_port) is False
    finally:
        srv.shutdown()


def test_server_down_is_unhealthy(tmp_path):
    srv = serve(200)
    port = srv.server_port
    srv.shutdown(); srv.server_close()
    (tmp_path / "m").write_text("1")
    assert run(tmp_path / "m", port) is False


def test_module_imports_only_the_standard_library():
    import ast
    import sys
    import app.healthcheck as h
    mods = {n.names[0].name.split(".")[0] if isinstance(n, ast.Import) else n.module.split(".")[0]
            for n in ast.walk(ast.parse(open(h.__file__).read())) if isinstance(n, (ast.Import, ast.ImportFrom))}
    assert mods <= set(sys.stdlib_module_names)
