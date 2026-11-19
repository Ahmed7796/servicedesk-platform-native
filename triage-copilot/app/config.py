from typing import Literal

from pydantic import SecretStr, ValidationError, field_validator
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
    gemini_model: str = "gemini-3.5-flash-lite"
    llm_timeout_seconds: float = 20.0
    triage_max_tool_rounds: int = 4
    triage_total_timeout_seconds: float = 60.0
    copilot_mode: Literal["llm", "canned"] = "llm"  # canned: rule-based demo suggestions, no LLM and no key

    @field_validator("copilot_mode", mode="before")
    @classmethod
    def _mode_text(cls, value):
        if isinstance(value, str):
            value = value.strip().lower()
            return value or "llm"  # blank (an empty Compose default) means the default
        return value

    @field_validator("gemini_api_key", mode="before")
    @classmethod
    def _blank_key_is_no_key(cls, value):
        # Compose passes GEMINI_API_KEY= (empty) by default; that must mean "no key", not a key of "".
        if isinstance(value, str) and not value.strip():
            return None
        return value


def get_settings() -> Settings:
    """Built on demand, so /health never needs credentials. Fails fast naming variables, never values."""
    try:
        return Settings(_env_file=None)
    except ValidationError as exc:
        names = sorted({str(e["loc"][0]).upper() for e in exc.errors() if e["loc"]})
        raise ConfigError(f"Missing or invalid environment variables: {', '.join(names)}") from None


class ConfigError(Exception):
    """A required environment variable is missing or invalid. Names variables, never values."""


class BrokerSettings(BaseSettings):
    """RabbitMQ and Redis connection, same variable names as notification-service. No defaults for host or login."""

    model_config = SettingsConfigDict(extra="ignore", hide_input_in_errors=True)

    rabbitmq_host: str
    rabbitmq_user: str
    rabbitmq_password: SecretStr
    redis_host: str
    rabbitmq_port: int = 5672
    redis_port: int = 6379
    copilot_ready_file: str = "/tmp/copilot-ready"  # readiness marker; the image HEALTHCHECK reads the same name

    @field_validator("rabbitmq_host", "rabbitmq_user", "rabbitmq_password", "redis_host", mode="before")
    @classmethod
    def _blank_is_missing(cls, value):
        if isinstance(value, str) and not value.strip():
            raise ValueError("blank")
        return value


def get_broker_settings() -> BrokerSettings:
    """Fail fast at startup with the names of the bad variables (not their values)."""
    try:
        return BrokerSettings(_env_file=None)
    except ValidationError as exc:
        names = sorted({str(e["loc"][0]).upper() for e in exc.errors() if e["loc"]})
        raise ConfigError(f"Missing or invalid environment variables: {', '.join(names)}") from None
