"""RabbitMQ consumer for request.submitted. Maps each handler Decision onto the broker; holds no triage logic.

Rule 6: the only thing this module does with a message is call handler.handle. Logs carry class names and fixed text
only: never the body or str(exc).
"""
import asyncio
import logging
from collections.abc import Awaitable, Callable

import aio_pika

from app.config import BrokerSettings
from app.handler import Action, Deps, handle as default_handle

log = logging.getLogger("app.consumer")

EXCHANGE = "servicedesk.events"
DLX = "copilot.dlx"
QUEUE = "copilot.triage"
DLQ = "copilot.triage.dlq"
ROUTING_KEY = "request.submitted"
PREFETCH = 2

Hook = Callable[[], Awaitable[None]]


async def declare_topology(channel):
    """Exchange first (contract: every consumer declares it, same arguments as the API), then DLX/DLQ, then the queue."""
    await channel.set_qos(prefetch_count=PREFETCH)
    exchange = await channel.declare_exchange(EXCHANGE, aio_pika.ExchangeType.TOPIC, durable=True)
    dlx = await channel.declare_exchange(DLX, aio_pika.ExchangeType.FANOUT, durable=True)
    dlq = await channel.declare_queue(DLQ, durable=True)
    await dlq.bind(dlx)
    queue = await channel.declare_queue(QUEUE, durable=True, arguments={"x-dead-letter-exchange": DLX})
    await queue.bind(exchange, routing_key=ROUTING_KEY)
    return queue


def robust_connector(settings: BrokerSettings):
    async def connect():
        return await aio_pika.connect_robust(
            host=settings.rabbitmq_host, port=settings.rabbitmq_port, login=settings.rabbitmq_user,
            password=settings.rabbitmq_password.get_secret_value(), reconnect_interval=5)
    return connect


async def _no_hook() -> None:
    return None


class Consumer:
    def __init__(self, deps: Deps, *, connect: Callable[[], Awaitable] | None = None, handle=default_handle,
                 on_ready: Hook = _no_hook, on_lost: Hook = _no_hook, ready_retry_seconds: float = 2.0) -> None:
        self._deps, self._connect, self._handle = deps, connect, handle
        self._on_ready, self._on_lost = on_ready, on_lost
        self._retry_seconds = ready_retry_seconds
        self._retry_task: asyncio.Task | None = None
        self._stopped = False
        self._gen = 0  # bumped by every loss, reconnect and stop: a late on_ready from an older one must not stand
        self._stopping = asyncio.Event()
        self._connection = None
        self._channel = None

    async def start(self) -> None:
        """Connect, declare, consume, then signal ready. On failure close the connection (a robust one would keep
        reconnecting in the background) and re-raise: the process must not look healthy."""
        self._connection = await self._connect()
        try:
            self._channel = await self._connection.channel()
            queue = await declare_topology(self._channel)
            await queue.consume(self.on_message)
            self._connection.close_callbacks.add(self._closed)
            self._connection.reconnect_callbacks.add(self._reconnected)
            await self._signal_ready()
        except BaseException:
            await self.stop()
            raise

    async def stop(self) -> None:
        if self._stopped:
            return
        self._stopped = True
        self._gen += 1
        await self._cancel_retry()  # first, so it cannot write the marker again
        await self._lost()  # readiness goes before anything closes
        self._stopping.set()  # wakes any backoff sleep
        for closer in (self._channel, self._connection):
            if closer is not None:
                try:
                    await closer.close()
                except Exception as exc:
                    log.warning("close failed (%s)", type(exc).__name__)

    async def _lost(self) -> None:
        try:
            await self._on_lost()
        except Exception as exc:
            log.warning("readiness hook failed (%s)", type(exc).__name__)

    async def _cancel_retry(self) -> None:
        task, self._retry_task = self._retry_task, None
        if task is not None and task is not asyncio.current_task():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

    async def _signal_ready(self) -> None:
        """Run on_ready; if a loss, reconnect or stop happened while it ran, undo it. aio-pika runs the connection
        callbacks as separate futures, so they can overlap with this one."""
        gen = self._gen
        await self._on_ready()
        if gen != self._gen or self._stopped:
            await self._lost()

    async def _closed(self, *_: object) -> None:
        self._gen += 1
        await self._cancel_retry()
        await self._lost()

    async def _reconnected(self, *_: object) -> None:
        self._gen += 1
        await self._cancel_retry()
        try:
            await self._signal_ready()
        except Exception as exc:  # e.g. Redis still down: keep checking until it answers or we stop
            log.warning("not ready after reconnect (%s), will retry", type(exc).__name__)
            self._retry_task = asyncio.create_task(self._retry_ready())

    async def _retry_ready(self) -> None:
        while not self._stopping.is_set():
            await self._backoff(self._retry_seconds)
            try:
                await self._signal_ready()
                return
            except Exception as exc:
                log.warning("still not ready (%s)", type(exc).__name__)

    async def on_message(self, message) -> None:
        try:
            decision = await self._handle(message.body, self._deps)
        except Exception as exc:
            # The handler turns store and agent faults into Decisions, so this is a bug. Requeueing would loop a
            # poison message forever: dead-letter it.
            log.error("handler bug (%s), dead-lettered", type(exc).__name__)
            await self._settle(message.reject(requeue=False))
            return
        if decision.action is Action.ACK:
            await self._settle(message.ack())
        elif decision.action is Action.RETRY:
            await self._backoff(decision.delay_seconds)
            await self._settle(message.nack(requeue=True))
        else:
            await self._settle(message.reject(requeue=False))

    async def _backoff(self, seconds: float) -> None:
        try:
            await asyncio.wait_for(self._stopping.wait(), seconds)  # returns early on stop()
        except TimeoutError:
            pass

    async def _settle(self, call: Awaitable[None]) -> None:
        try:
            await call
        except Exception as exc:  # channel gone: the broker redelivers an unsettled message
            log.warning("settle failed (%s)", type(exc).__name__)
