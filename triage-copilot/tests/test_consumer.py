import asyncio
import json
import logging
from pathlib import Path

import httpx
import pytest

import app.handler as handler_mod
from app.agent import TriageOutcome, triage
from app.consumer import Consumer
from app.handler import Action, Decision, Deps
from app.tools import TriageTools
from tests.fake_broker import FakeChannel, FakeConnection, FakeMessage
from tests.fake_llm import FakeLlm
from tests.fake_store import FakeStore
from tests.test_agent import GOOD, RID as TRID, cfg, post_ok, api_calls
from tests.test_tools import serve
from app.llm import LlmReply, ToolCall

pytestmark = pytest.mark.anyio
EXAMPLE = Path(__file__).resolve().parents[2] / "contracts/events/examples/request.submitted.json"
EID = json.loads(EXAMPLE.read_text())["eventId"]


def fixed(decision):
    async def handle(body, deps):
        return decision
    return handle


def consumer(handle, **kw):
    return Consumer(Deps(store=FakeStore(), triage=None), handle=handle, **kw)


@pytest.mark.parametrize("decision,expected", [
    (Decision(Action.ACK), ("ack", None)),
    (Decision(Action.RETRY, 0.01), ("nack", True)),
    (Decision(Action.DEAD_LETTER), ("reject", False)),
])
async def test_decision_mapping(decision, expected):
    msg = FakeMessage()
    await consumer(fixed(decision)).on_message(msg)
    assert msg.settled == [expected]


async def test_retry_waits_for_the_delay_before_nack():
    msg = FakeMessage()
    task = asyncio.create_task(consumer(fixed(Decision(Action.RETRY, 0.2))).on_message(msg))
    await asyncio.sleep(0.05)
    assert msg.settled == []
    await task
    assert msg.settled == [("nack", True)]


async def test_shutdown_interrupts_the_backoff_and_requeues_promptly():
    msg, c = FakeMessage(), consumer(fixed(Decision(Action.RETRY, 30)))
    task = asyncio.create_task(c.on_message(msg))
    await asyncio.sleep(0.05)
    await c.stop()
    await asyncio.wait_for(task, 1)
    assert msg.settled == [("nack", True)]


async def test_poison_message_is_dead_lettered_and_logs_class_only(caplog):
    async def boom(body, deps):
        raise RuntimeError("SENTINEL-EXC with body")
    msg = FakeMessage(b"SENTINEL-BODY")
    with caplog.at_level(logging.DEBUG):
        await consumer(boom).on_message(msg)
    assert msg.settled == [("reject", False)]
    text = "\n".join(r.getMessage() + repr(r.args) for r in caplog.records)
    assert "RuntimeError" in text and "SENTINEL" not in text
    assert all(r.exc_info is None for r in caplog.records)


async def test_settle_failure_is_logged_not_raised(caplog):
    msg = FakeMessage()
    msg.fail_settle = True
    with caplog.at_level(logging.DEBUG):
        await consumer(fixed(Decision(Action.ACK))).on_message(msg)  # must not raise
    assert "RuntimeError" in caplog.text and "SENTINEL" not in caplog.text


async def test_cancellation_during_handle_propagates_unsettled():
    async def slow(body, deps):
        await asyncio.sleep(30)
    msg = FakeMessage()
    task = asyncio.create_task(consumer(slow).on_message(msg))
    await asyncio.sleep(0.02)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert msg.settled == []  # the broker redelivers when the channel closes


async def test_cancellation_during_backoff_propagates_unsettled():
    msg = FakeMessage()
    task = asyncio.create_task(consumer(fixed(Decision(Action.RETRY, 30))).on_message(msg))
    await asyncio.sleep(0.02)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert msg.settled == []


async def test_attempts_across_redeliveries_nack_nack_reject(monkeypatch):
    monkeypatch.setattr(handler_mod, "BACKOFF_SECONDS", (0.01, 0.01))
    store = FakeStore()
    calls = []

    async def failing(rid):
        calls.append(rid)
        return TriageOutcome("failed", "ServiceDesk API unavailable", retryable=True)

    c = Consumer(Deps(store=store, triage=failing))
    seen = []
    for _ in range(3):  # the broker redelivers the same body after each nack
        msg = FakeMessage(EXAMPLE.read_bytes())
        await c.on_message(msg)
        seen.append(msg.settled[0])
    assert seen == [("nack", True), ("nack", True), ("reject", False)] and len(calls) == 3
    assert EID not in store.processed


async def test_rule_6_exactly_one_triage_post_and_no_other_write(tools_factory):
    deps, stub = tools_factory
    msg = FakeMessage(json.dumps({**json.loads(EXAMPLE.read_text()), "requestId": TRID}).encode())
    await Consumer(deps).on_message(msg)
    assert msg.settled == [("ack", None)]
    writes = [c for c in api_calls(stub) if c[0] != "GET"]
    assert writes == [("POST", f"/api/service-requests/{TRID}/triage")]


@pytest.fixture
def tools_factory(client, stub):
    serve(stub)
    post_ok(stub)
    llm = FakeLlm([LlmReply(tool_calls=[ToolCall("get_request", {"request_id": TRID})]), LlmReply()],
                  final=json.dumps(GOOD))
    settings = cfg()

    async def run(rid):
        return await triage(rid, llm, TriageTools(client), client, settings)

    return Deps(store=FakeStore(), triage=run), stub


async def test_start_declares_consumes_then_signals_ready_and_stop_closes():
    ch = FakeChannel()
    conn = FakeConnection(ch)
    order = []

    async def connect():
        order.append("connect")
        return conn

    async def ready():
        order.append(("ready", [k for k, _ in ch.log if k in ("bind", "consume")]))

    async def lost():
        order.append("lost")

    c = Consumer(Deps(store=FakeStore(), triage=None), connect=connect, on_ready=ready, on_lost=lost)
    await c.start()
    assert order == ["connect", ("ready", ["bind", "bind", "consume"])]  # ready only after both binds
    assert ch.queues["copilot.triage"].callback == c.on_message
    # connection-loss and reconnect hooks are registered
    (close_cb,) = conn.close_callbacks.items
    (reconnect_cb,) = conn.reconnect_callbacks.items
    await close_cb(conn, None)
    assert order[-1] == "lost"
    await reconnect_cb(conn, conn)
    assert order[-1][0] == "ready"
    await c.stop()
    assert ch.closed and conn.closed


async def test_failed_bind_means_never_ready():
    class BadChannel(FakeChannel):
        async def declare_queue(self, name, **kw):
            q = await super().declare_queue(name, **kw)
            if name == "copilot.triage":
                async def bind(*a, **k):
                    raise ConnectionError("no")
                q.bind = bind
            return q

    ready = []

    async def connect():
        return FakeConnection(BadChannel())

    async def on_ready():
        ready.append(1)

    c = Consumer(Deps(store=FakeStore(), triage=None), connect=connect, on_ready=on_ready)
    with pytest.raises(ConnectionError):
        await c.start()
    assert ready == []
