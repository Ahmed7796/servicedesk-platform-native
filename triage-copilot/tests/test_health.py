from fastapi.testclient import TestClient

from app.main import app


def test_health_returns_ok():
    with TestClient(app) as c:
        resp = c.get("/health")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}


def test_health_needs_no_env_or_token(monkeypatch):
    for name in ("SERVICEDESK_API_URL", "TRIAGE_CLIENT_ID", "TRIAGE_CLIENT_SECRET"):
        monkeypatch.delenv(name, raising=False)
    with TestClient(app) as c:
        assert c.get("/health").status_code == 200
