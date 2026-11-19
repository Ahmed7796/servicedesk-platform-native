import logging
import os
import sys
from collections.abc import Callable
from contextlib import asynccontextmanager
from dataclasses import dataclass, field

import httpx
import redis.asyncio as aioredis
from fastapi import FastAPI

from app.agent import triage
from app.canned import CannedLlm
from app.config import BrokerSettings, get_broker_settings, get_settings
from app.dedup import RedisProcessedStore
from app.consumer import Consumer, robust_connector
from app.gemini import build_llm
from app.handler import Deps
from app.readiness import Marker, Readiness
from app.servicedesk_client import ServiceDeskClient, ServiceTokenProvider
from app.tools import TriageTools

log = logging.getLogger("app.main")
REDIS_TIMEOUT_SECONDS = 4.0  # a silent Redis must not block startup or the reconnect callback


def configure_logging() -> None:
    """INFO for our own loggers only. Under uvicorn the root logger has no handler and stays at WARNING, so without
    this the 'triage posted' and 'attempt N/3' lines never show. Root and uvicorn's loggers are left alone."""
    app_logger = logging.getLogger("app")
    app_logger.setLevel(logging.INFO)
    if not any(getattr(h, "_copilot", False) for h in app_logger.handlers):
        handler = logging.StreamHandler(sys.stderr)
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
        handler._copilot = True  # type: ignore[attr-defined]
        app_logger.addHandler(handler)


def consumer_enabled_from_env() -> bool:
    return os.environ.get("COPILOT_CONSUMER_ENABLED", "true").strip().lower() not in {"false", "0", "no", "off"}


def _make_redis(broker: BrokerSettings):
    return aioredis.Redis(host=broker.redis_host, port=broker.redis_port, decode_responses=True,
                          socket_timeout=REDIS_TIMEOUT_SECONDS, socket_connect_timeout=REDIS_TIMEOUT_SECONDS)


def _make_consumer(deps: Deps, connector, readiness: Readiness) -> Consumer:
    return Consumer(deps, connect=connector, on_ready=readiness.on_ready, on_lost=readiness.on_lost)


@dataclass
class Wiring:
    """The pieces the lifespan builds; tests replace them with fakes."""
    build_llm: Callable = build_llm
    make_redis: Callable = _make_redis
    make_consumer: Callable = _make_consumer


def create_app(consumer_enabled: bool | None = None, wiring: Wiring | None = None) -> FastAPI:
    configure_logging()
    w = wiring or Wiring()

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        enabled = consumer_enabled_from_env() if consumer_enabled is None else consumer_enabled
        if not enabled:
            yield
            return
        settings, broker = get_settings(), get_broker_settings()  # fail fast, before anything is created
        marker = Marker(broker.copilot_ready_file)
        marker.clear()  # a marker left by a previous run must never count
        closers: list[Callable] = []
        try:
            http = httpx.AsyncClient(base_url=settings.servicedesk_api_url, timeout=settings.http_timeout_seconds)
            closers.append(http.aclose)
            tokens = ServiceTokenProvider(http, settings.triage_client_id, settings.triage_client_secret,
                                          skew_seconds=settings.token_refresh_skew_seconds)
            client = ServiceDeskClient(http, tokens)
            canned = settings.copilot_mode == "canned"
            if canned:
                log.warning("canned demo mode: no LLM is called, suggestions are rule-based and marked [Canned demo]")
                llm = CannedLlm()
            else:
                llm = w.build_llm(settings)  # None: no key, so every event is skipped, marked and acked

            async def run_triage(request_id: str):
                return await triage(request_id, llm, TriageTools(client), client, settings)

            redis = w.make_redis(broker)
            closers.append(redis.aclose)
            deps = Deps(store=RedisProcessedStore(redis), triage=None if llm is None else run_triage)
            readiness = Readiness(marker, redis.ping)
            consumer = w.make_consumer(deps, robust_connector(broker), readiness)
            closers.append(consumer.stop)
            await consumer.start()
            yield
        finally:
            for close in reversed(closers):  # a failing close must not skip the rest
                try:
                    await close()
                except Exception as exc:
                    log.warning("shutdown step failed (%s)", type(exc).__name__)

    app = FastAPI(title="ServiceDesk Triage Copilot", lifespan=lifespan)

    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "ok"}

    return app


app = create_app()
