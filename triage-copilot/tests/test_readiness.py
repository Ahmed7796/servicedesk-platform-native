import asyncio
import os

import pytest

from app.consumer import Consumer
from app.handler import Deps
from app.readiness import Marker, Readiness
from tests.fake_broker import FakeChannel, FakeConnection
from tests.fake_store import FakeStore

pytestmark = pytest.mark.anyio


class Ping:
    def __init__(self, *results):
        self.results, self.calls = list(results), 0

    async def __call__(self):
        self.calls += 1
        r = self.results.pop(0) if len(self.results) > 1 else self.results[0]
        if r == "fail":
            raise ConnectionError("redis SENTINEL down")


def make(tmp_path, ping, *, channel=None, retry=0.01):
    path = tmp_path / "ready"
    marker = Marker(str(path))
    ready = Readiness(marker, ping)
    conn = FakeConnection(channel or FakeChannel())

    async def connect():
        return conn

    c = Consumer(Deps(store=FakeStore(), triage=None), connect=connect, on_ready=ready.on_ready,
                 on_lost=ready.on_lost, ready_retry_seconds=retry)
    return c, conn, path


async def settle():
    await asyncio.sleep(0.1)


def test_marker_set_and_clear_are_atomic_and_idempotent(tmp_path):
    p = tmp_path / "m"
    m = Marker(str(p))
    m.clear()  # missing is fine
    m.set(); m.set()
    assert p.exists() and os.listdir(tmp_path) == ["m"]  # no temp file left behind
    m.clear(); m.clear()
    assert not p.exists()


async def test_marker_written_after_bind_and_ping(tmp_path):
    c, conn, path = make(tmp_path, Ping("ok"))
    assert not path.exists()
    await c.start()
    assert path.exists()
    await c.stop()
    assert not path.exists()


async def test_no_marker_if_ping_fails_and_connection_is_closed(tmp_path):
    c, conn, path = make(tmp_path, Ping("fail"))
    with pytest.raises(ConnectionError):
        await c.start()
    assert not path.exists() and conn.closed  # the robust connection must not keep reconnecting


async def test_no_marker_if_bind_fails_and_connection_is_closed(tmp_path):
    class Bad(FakeChannel):
        async def declare_queue(self, name, **kw):
            q = await super().declare_queue(name, **kw)
            async def bind(*a, **k):
                raise RuntimeError("no")
            q.bind = bind
            return q
    c, conn, path = make(tmp_path, Ping("ok"), channel=Bad())
    with pytest.raises(RuntimeError):
        await c.start()
    assert not path.exists() and conn.closed


async def test_connection_loss_removes_marker_and_reconnect_restores_it(tmp_path):
    c, conn, path = make(tmp_path, Ping("ok"))
    await c.start()
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await close_cb(conn, None)
    assert not path.exists()
    await reconnect_cb(conn, conn)
    assert path.exists()
    await c.stop()


async def test_reconnect_with_failing_ping_retries_until_it_succeeds(tmp_path):
    ping = Ping("ok", "fail", "fail", "ok")
    c, conn, path = make(tmp_path, ping)
    await c.start()
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await close_cb(conn, None)
    await reconnect_cb(conn, conn)
    assert not path.exists()  # first ping failed
    await settle()
    assert path.exists() and ping.calls == 4
    await c.stop()


async def test_stop_cancels_the_retry_task_and_removes_marker_first(tmp_path):
    ping = Ping("ok", "fail")
    c, conn, path = make(tmp_path, ping, retry=0.01)
    await c.start()
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await reconnect_cb(conn, conn)
    await asyncio.sleep(0.03)
    await c.stop()
    calls = ping.calls
    await settle()
    assert ping.calls == calls and not path.exists()  # no pings after stop


async def test_stop_clears_marker_before_closing_anything(tmp_path):
    events = []
    c, conn, path = make(tmp_path, Ping("ok"))
    await c.start()
    orig = conn.close

    async def close():
        events.append(("closed", path.exists()))
        await orig()
    conn.close = close
    await c.stop()
    assert events == [("closed", False)]


async def test_loss_cancels_pending_retry(tmp_path):
    ping = Ping("ok", "fail")
    c, conn, path = make(tmp_path, ping)
    await c.start()
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await reconnect_cb(conn, conn)
    await close_cb(conn, None)
    calls = ping.calls
    await settle()
    assert ping.calls == calls and not path.exists()
    await c.stop()


class GatedPing:
    """First call passes; later calls wait until released, so a loss can land while on_ready is running."""

    def __init__(self):
        self.gate = asyncio.Event()
        self.entered = asyncio.Event()
        self.calls = 0

    async def __call__(self):
        self.calls += 1
        if self.calls > 1:
            self.entered.set()
            await self.gate.wait()


async def test_loss_during_reconnect_ping_does_not_leave_a_marker(tmp_path):
    ping = GatedPing()
    c, conn, path = make(tmp_path, ping)
    await c.start()
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    task = asyncio.create_task(reconnect_cb(conn, conn))
    await ping.entered.wait()
    await close_cb(conn, None)  # the loss lands while the ping is in flight
    ping.gate.set()
    await task
    assert not path.exists()
    await c.stop()


async def test_stop_during_reconnect_ping_does_not_leave_a_marker(tmp_path):
    ping = GatedPing()
    c, conn, path = make(tmp_path, ping)
    await c.start()
    (reconnect_cb,) = conn.reconnect_callbacks.items
    task = asyncio.create_task(reconnect_cb(conn, conn))
    await ping.entered.wait()
    await c.stop()
    ping.gate.set()
    await task
    assert not path.exists()


async def test_loss_during_retry_ping_does_not_leave_a_marker(tmp_path):
    class P(GatedPing):
        async def __call__(self):
            self.calls += 1
            if self.calls == 2:
                raise ConnectionError("down")  # the reconnect check fails, a retry starts
            if self.calls > 2:
                self.entered.set()
                await self.gate.wait()

    ping = P()
    c, conn, path = make(tmp_path, ping)
    await c.start()
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await reconnect_cb(conn, conn)
    await ping.entered.wait()  # the retry task is inside its ping
    await close_cb(conn, None)
    ping.gate.set()
    await settle()
    assert not path.exists()
    await c.stop()


async def test_stop_twice_is_harmless(tmp_path):
    c, conn, path = make(tmp_path, Ping("ok"))
    await c.start()
    await c.stop()
    await c.stop()
    assert conn.closed and not path.exists()
