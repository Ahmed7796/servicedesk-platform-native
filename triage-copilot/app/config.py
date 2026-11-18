from pydantic import SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Read from environment variables (case-insensitive field names). Unknown vars are ignored."""

    model_config = SettingsConfigDict(extra="ignore", hide_input_in_errors=True)

    servicedesk_api_url: str
    triage_client_id: str
    triage_client_secret: SecretStr
    token_refresh_skew_seconds: int = 60
    http_timeout_seconds: float = 10.0
    gemini_api_key: SecretStr | None = None  # no key: the service runs and triage is skipped
    gemini_model: str = "gemini-2.5-flash"
    llm_timeout_seconds: float = 20.0
    triage_max_tool_rounds: int = 4
    triage_total_timeout_seconds: float = 60.0


def get_settings() -> Settings:
    # Built on demand, so /health never needs credentials.
    return Settings()
