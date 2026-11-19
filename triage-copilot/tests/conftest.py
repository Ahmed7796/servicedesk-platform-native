import json
from datetime import datetime, timedelta, timezone

import httpx
import pytest

from app.servicedesk_client import ServiceDeskClient, ServiceTokenProvider

BASE = "http://api.test"
T0 = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)


@pytest.fixture
def anyio_backend():
    return "asyncio"


class Clock:
    def __init__(self) -> None:
        self.now = T0

    def __call__(self) -> datetime:
        return self.now

    def advance(self, **kw) -> None:
        self.now += timedelta(**kw)


class StubApi:
    """MockTransport that records every call and serves queued responses per (method, path)."""

    def __init__(self, clock: Clock) -> None:
        self._clock = clock
        self.calls: list[httpx.Request] = []
        self._queues: dict[tuple[str, str], list[httpx.Response]] = {}
        self._token_n = 0
        self.token_status = 200
        self.token_lifetime = timedelta(minutes=30)
        self.expires_format = "z"  # "z" or "offset"

    def queue(self, method: str, path: str, *responses: httpx.Response) -> None:
        self._queues.setdefault((method, path), []).extend(responses)

    def count(self, method: str, path: str) -> int:
        return sum(1 for c in self.calls if c.method == method and c.url.path == path)

    def _handle(self, request: httpx.Request) -> httpx.Response:
        self.calls.append(request)
        if request.method == "POST" and request.url.path == "/api/auth/service-token":
            if self.token_status != 200:
                return httpx.Response(self.token_status, json={"title": "Unauthorized"})
            self._token_n += 1
            exp = self._clock.now + self.token_lifetime
            stamp = exp.strftime("%Y-%m-%dT%H:%M:%SZ" if self.expires_format == "z" else "%Y-%m-%dT%H:%M:%S+00:00")
            return httpx.Response(200, json={"accessToken": f"tok-{self._token_n}", "expiresAt": stamp})
        queue = self._queues.get((request.method, request.url.path))
        if not queue:
            return httpx.Response(404)
        return queue.pop(0)


@pytest.fixture
def clock():
    return Clock()


@pytest.fixture
def stub(clock):
    return StubApi(clock)


@pytest.fixture
async def http(stub):
    async with httpx.AsyncClient(base_url=BASE, transport=httpx.MockTransport(stub._handle)) as c:
        yield c


@pytest.fixture
def tokens(http, clock):
    return ServiceTokenProvider(http, "triage", "s3cret-value", skew_seconds=60, clock=clock)


@pytest.fixture
def client(http, tokens):
    return ServiceDeskClient(http, tokens)


def body(request: httpx.Request) -> dict:
    return json.loads(request.content)


@pytest.fixture(autouse=True)
def _consumer_off(monkeypatch):
    # The module-level app must never try to reach a broker in tests.
    monkeypatch.setenv("COPILOT_CONSUMER_ENABLED", "false")
