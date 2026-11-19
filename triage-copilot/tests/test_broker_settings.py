import pytest

from app.config import BrokerSettings, ConfigError, get_broker_settings

ALL = {"RABBITMQ_HOST": "rabbitmq", "RABBITMQ_USER": "svc", "RABBITMQ_PASSWORD": "pw-SENTINEL", "REDIS_HOST": "redis"}


def env(monkeypatch, **over):
    for k in list(ALL) + ["RABBITMQ_PORT", "REDIS_PORT", "COPILOT_READY_FILE"]:
        monkeypatch.delenv(k, raising=False)
    for k, v in {**ALL, **over}.items():
        if v is not None:
            monkeypatch.setenv(k, v)


def test_reads_env_with_default_ports(monkeypatch):
    env(monkeypatch)
    s = get_broker_settings()
    assert (s.rabbitmq_host, s.rabbitmq_user, s.redis_host) == ("rabbitmq", "svc", "redis")
    assert (s.rabbitmq_port, s.redis_port) == (5672, 6379)
    assert s.rabbitmq_password.get_secret_value() == "pw-SENTINEL"


@pytest.mark.parametrize("name", list(ALL))
def test_missing_variable_is_named(monkeypatch, name):
    env(monkeypatch, **{name: None})
    with pytest.raises(ConfigError) as err:
        get_broker_settings()
    assert name in str(err.value)
    assert "pw-SENTINEL" not in str(err.value)


def test_all_missing_are_listed_together(monkeypatch):
    env(monkeypatch, **{k: None for k in ALL})
    with pytest.raises(ConfigError) as err:
        get_broker_settings()
    assert all(k in str(err.value) for k in ALL)


def test_blank_value_counts_as_missing(monkeypatch):
    env(monkeypatch, RABBITMQ_USER="  ")
    with pytest.raises(ConfigError) as err:
        get_broker_settings()
    assert "RABBITMQ_USER" in str(err.value)


def test_password_not_in_repr(monkeypatch):
    env(monkeypatch)
    assert "pw-SENTINEL" not in repr(get_broker_settings())


def test_bad_port_does_not_echo_value(monkeypatch):
    env(monkeypatch, RABBITMQ_PORT="SENTINEL-port")
    with pytest.raises(ConfigError) as err:
        get_broker_settings()
    assert "RABBITMQ_PORT" in str(err.value) and "SENTINEL" not in str(err.value)
