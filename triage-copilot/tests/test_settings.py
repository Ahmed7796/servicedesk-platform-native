import pytest
from pydantic import ValidationError

from app.config import Settings


def test_missing_secret_raises_clearly(monkeypatch):
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.setenv("TRIAGE_CLIENT_ID", "triage")
    monkeypatch.delenv("TRIAGE_CLIENT_SECRET", raising=False)
    with pytest.raises(ValidationError) as err:
        Settings(_env_file=None)
    assert "triage_client_secret" in str(err.value)


def test_reads_env(monkeypatch):
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.setenv("TRIAGE_CLIENT_ID", "triage")
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", "x")
    s = Settings(_env_file=None)
    assert s.servicedesk_api_url == "http://api:8080"
    assert s.token_refresh_skew_seconds == 60


def test_secret_not_leaked_in_validation_error(monkeypatch):
    secret = "super-secret-value-123"
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.delenv("TRIAGE_CLIENT_ID", raising=False)
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", secret)
    with pytest.raises(ValidationError) as err:
        Settings(_env_file=None)
    assert secret not in str(err.value)
    assert secret not in repr(err.value)


def test_secret_not_in_repr(monkeypatch):
    secret = "super-secret-value-123"
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.setenv("TRIAGE_CLIENT_ID", "triage")
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", secret)
    s = Settings(_env_file=None)
    assert secret not in repr(s)
    assert s.triage_client_secret.get_secret_value() == secret
