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


def test_gemini_settings_default_and_key_is_hidden(monkeypatch):
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.setenv("TRIAGE_CLIENT_ID", "triage")
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", "x")
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    s = Settings(_env_file=None)
    assert s.gemini_api_key is None and s.triage_max_tool_rounds == 4
    assert s.gemini_model == "gemini-3.5-flash-lite"
    monkeypatch.setenv("GEMINI_API_KEY", "gk-visible?")
    assert "gk-visible?" not in repr(Settings(_env_file=None))


def _base(monkeypatch):
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.setenv("TRIAGE_CLIENT_ID", "triage")
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", "x")


def test_copilot_mode_defaults_to_llm_and_tolerates_case_and_blank(monkeypatch):
    _base(monkeypatch)
    monkeypatch.delenv("COPILOT_MODE", raising=False)
    assert Settings(_env_file=None).copilot_mode == "llm"
    for raw, want in [(" Canned ", "canned"), ("LLM", "llm"), ("", "llm"), ("  ", "llm")]:
        monkeypatch.setenv("COPILOT_MODE", raw)
        assert Settings(_env_file=None).copilot_mode == want


def test_bad_copilot_mode_fails_naming_only_the_variable(monkeypatch):
    from app.config import ConfigError, get_settings
    _base(monkeypatch)
    monkeypatch.setenv("COPILOT_MODE", "bogus-VALUE-123")
    with pytest.raises(ConfigError) as err:
        get_settings()
    assert "COPILOT_MODE" in str(err.value) and "bogus-VALUE-123" not in str(err.value)
    assert err.value.__cause__ is None


def test_get_settings_error_names_missing_variables_without_values(monkeypatch):
    from app.config import ConfigError, get_settings
    monkeypatch.setenv("SERVICEDESK_API_URL", "http://api:8080")
    monkeypatch.delenv("TRIAGE_CLIENT_ID", raising=False)
    monkeypatch.setenv("TRIAGE_CLIENT_SECRET", "secret-VALUE-9")
    with pytest.raises(ConfigError) as err:
        get_settings()
    assert "TRIAGE_CLIENT_ID" in str(err.value) and "secret-VALUE-9" not in str(err.value)
