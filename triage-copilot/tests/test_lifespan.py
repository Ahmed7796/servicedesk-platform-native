import logging

import pytest
from fastapi.testclient import TestClient

from app.config import ConfigError
from app.main import Wiring, create_app

ENV = {"SERVICEDESK_API_URL": "http://api.test", "TRIAGE_CLIENT_ID": "triage",
       "TRIAGE_CLIENT_SECRET": "sec-SENTINEL", "GEMINI_API_KEY": "gk-SENTINEL",
       "RABBITMQ_HOST": "rabbitmq", "RABBITMQ_USER": "svc", "RABBITMQ_PASSWORD": "pw-SENTINEL", "REDIS_HOST": "redis"}


class Rec:
    def __init__(self):
        self.events = []
        self.fail_close = set()
        self.consumer = None
        self.deps = None
        self.connector = None


@pytest.fixture
def http_closes(monkeypatch):
    import httpx
    import app.main as m
    closes = []

    class Rec(httpx.AsyncClient):
        async def aclose(self):
            closes.append(1)
            await super().aclose()

    monkeypatch.setattr(m.httpx, "AsyncClient", Rec)
    return closes


def wiring(rec, *, llm=object(), start_error=None):
    class FakeRedis:
        async def ping(self):
            rec.events.append("ping")

        async def aclose(self):
            rec.events.append("close redis")
            if "redis" in rec.fail_close:
                raise RuntimeError("redis close SENTINEL-CLOSE")

    class FakeConsumer:
        def __init__(self, deps, connector, readiness):
            rec.consumer, rec.deps, rec.connector = self, deps, connector

        async def start(self):
            rec.events.append("start consumer")
            if start_error:
                raise start_error

        async def stop(self):
            rec.events.append("stop consumer")

    return Wiring(build_llm=lambda s: llm, make_redis=lambda b: FakeRedis(),
                  make_consumer=lambda deps, connector, readiness: FakeConsumer(deps, connector, readiness))


def set_env(monkeypatch, **over):
    for k, v in {**ENV, **over}.items():
        if v is None:
            monkeypatch.delenv(k, raising=False)
        else:
            monkeypatch.setenv(k, v)


def test_disabled_needs_no_broker_settings_and_health_is_ok(monkeypatch):
    for k in ENV:
        monkeypatch.delenv(k, raising=False)
    with TestClient(create_app()) as c:  # conftest sets COPILOT_CONSUMER_ENABLED=false
        assert c.get("/health").json() == {"status": "ok"}


def test_enabled_starts_in_order_and_closes_in_reverse(monkeypatch):
    set_env(monkeypatch)
    rec = Rec()
    with TestClient(create_app(consumer_enabled=True, wiring=wiring(rec))) as c:
        assert c.get("/health").status_code == 200
        assert rec.events == ["start consumer"]
    assert rec.events == ["start consumer", "stop consumer", "close redis"]  # consumer, then Redis; http last
    assert rec.deps.triage is not None
    assert rec.connector is not None  # robust_connector is passed, never None


def test_no_llm_means_triage_is_none(monkeypatch):
    set_env(monkeypatch)
    rec = Rec()
    with TestClient(create_app(consumer_enabled=True, wiring=wiring(rec, llm=None))):
        pass
    assert rec.deps.triage is None


def test_failing_close_does_not_skip_the_rest(monkeypatch, caplog):
    set_env(monkeypatch)
    rec = Rec()
    rec.fail_close = {"redis"}
    with caplog.at_level(logging.DEBUG):
        with TestClient(create_app(consumer_enabled=True, wiring=wiring(rec))):
            pass
    assert rec.events[-2:] == ["stop consumer", "close redis"]
    assert "RuntimeError" in caplog.text and "SENTINEL" not in caplog.text


def test_start_failure_closes_what_was_opened_and_raises(monkeypatch):
    set_env(monkeypatch)
    rec = Rec()
    with pytest.raises(ConnectionError):
        with TestClient(create_app(consumer_enabled=True, wiring=wiring(rec, start_error=ConnectionError("x")))):
            pass
    assert rec.events == ["start consumer", "stop consumer", "close redis"]


@pytest.mark.parametrize("name", ["RABBITMQ_HOST", "RABBITMQ_USER", "RABBITMQ_PASSWORD", "REDIS_HOST"])
def test_missing_broker_variable_fails_fast_before_creating_anything(monkeypatch, name):
    set_env(monkeypatch, **{name: None})
    rec = Rec()
    with pytest.raises(ConfigError) as err:
        with TestClient(create_app(consumer_enabled=True, wiring=wiring(rec))):
            pass
    assert name in str(err.value) and rec.events == []


def test_logs_hold_no_secret_values(monkeypatch, caplog):
    set_env(monkeypatch)
    with caplog.at_level(logging.DEBUG):
        with TestClient(create_app(consumer_enabled=True, wiring=wiring(Rec()))):
            pass
    assert "SENTINEL" not in caplog.text


@pytest.mark.parametrize("value,expected", [(None, True), ("true", True), ("1", True), ("false", False),
                                            ("FALSE", False), ("0", False), ("no", False)])
def test_switch_parsing(monkeypatch, value, expected):
    from app.main import consumer_enabled_from_env
    if value is None:
        monkeypatch.delenv("COPILOT_CONSUMER_ENABLED", raising=False)
    else:
        monkeypatch.setenv("COPILOT_CONSUMER_ENABLED", value)
    assert consumer_enabled_from_env() is expected


def test_http_client_is_closed_on_normal_shutdown(monkeypatch, http_closes):
    set_env(monkeypatch)
    with TestClient(create_app(consumer_enabled=True, wiring=wiring(Rec()))):
        assert http_closes == []
    assert http_closes == [1]


def test_http_client_is_closed_when_start_fails(monkeypatch, http_closes):
    set_env(monkeypatch)
    with pytest.raises(ConnectionError):
        with TestClient(create_app(consumer_enabled=True, wiring=wiring(Rec(), start_error=ConnectionError("x")))):
            pass
    assert http_closes == [1]


def test_stale_marker_is_removed_at_startup(monkeypatch, tmp_path):
    marker = tmp_path / "ready"
    marker.write_text("stale")
    set_env(monkeypatch, COPILOT_READY_FILE=str(marker))
    seen = []
    rec = Rec()
    w = wiring(rec)
    orig = w.make_consumer

    def make(deps, connector, readiness):
        seen.append(marker.exists())  # checked before start(): the fake never writes the marker
        return orig(deps, connector, readiness)

    w.make_consumer = make
    with TestClient(create_app(consumer_enabled=True, wiring=w)):
        pass
    assert seen == [False]


def test_redis_client_has_timeouts():
    from app.config import BrokerSettings
    from app.main import _make_redis
    b = BrokerSettings(_env_file=None, rabbitmq_host="h", rabbitmq_user="u", rabbitmq_password="p", redis_host="r")
    kw = _make_redis(b).connection_pool.connection_kwargs
    from app.main import REDIS_TIMEOUT_SECONDS
    assert kw["socket_timeout"] == kw["socket_connect_timeout"] == REDIS_TIMEOUT_SECONDS <= 10


@pytest.fixture
def clean_app_logger():
    lg = logging.getLogger("app")
    level, handlers = lg.level, list(lg.handlers)
    lg.setLevel(logging.NOTSET)
    for h in handlers:
        lg.removeHandler(h)
    yield lg
    lg.setLevel(level)
    for h in list(lg.handlers):
        lg.removeHandler(h)
    for h in handlers:
        lg.addHandler(h)


def test_create_app_sets_the_app_logger_to_info_once(clean_app_logger):
    root_level = logging.getLogger().level
    create_app()
    create_app()
    assert clean_app_logger.getEffectiveLevel() == logging.INFO
    assert len(clean_app_logger.handlers) == 1  # not added twice
    assert logging.getLogger().level == root_level  # root and uvicorn's loggers are left alone
