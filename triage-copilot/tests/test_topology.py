import aio_pika
import pytest

from app.consumer import declare_topology
from tests.fake_broker import FakeChannel

pytestmark = pytest.mark.anyio


async def test_topology():
    ch = FakeChannel()
    queue = await declare_topology(ch)
    ex = ch.exchanges["servicedesk.events"]
    assert ex.extra == {}  # no auto_delete or internal: either would conflict with the API's declaration
    assert (ex.type, ex.durable, ex.arguments) == (aio_pika.ExchangeType.TOPIC, True, None)  # same as the API
    dlx = ch.exchanges["copilot.dlx"]
    assert dlx.extra == {}
    assert (dlx.type, dlx.durable) == (aio_pika.ExchangeType.FANOUT, True)
    dlq = ch.queues["copilot.triage.dlq"]
    assert dlq.durable is True and dlq.binds == [("copilot.dlx", None)]
    assert queue is ch.queues["copilot.triage"]
    assert queue.durable is True and queue.arguments == {"x-dead-letter-exchange": "copilot.dlx"}
    assert queue.binds == [("servicedesk.events", "request.submitted")]  # that key only
    assert ch.prefetch == 2
    assert set(ch.queues) == {"copilot.triage", "copilot.triage.dlq"}


async def test_exchanges_are_declared_before_anything_binds():
    ch = FakeChannel()
    await declare_topology(ch)
    kinds = [k for k, _ in ch.log]
    first_bind = kinds.index("bind")
    assert ("exchange", "servicedesk.events") in ch.log[:first_bind]
    assert ("exchange", "copilot.dlx") in ch.log[:first_bind]
