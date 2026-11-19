"""Fakes for aio-pika objects. Record calls only; no network."""


class FakeMessage:
    def __init__(self, body: bytes = b"{}") -> None:
        self.body = body
        self.settled: list[tuple[str, bool | None]] = []
        self.fail_settle = False

    async def _settle(self, how: str, requeue: bool | None = None) -> None:
        if self.fail_settle:
            raise RuntimeError("channel closed SENTINEL-SETTLE")
        self.settled.append((how, requeue))

    async def ack(self):
        await self._settle("ack")

    async def nack(self, requeue: bool = True):
        await self._settle("nack", requeue)

    async def reject(self, requeue: bool = False):
        await self._settle("reject", requeue)


class FakeQueue:
    def __init__(self, name: str, log: list, arguments=None, durable=None) -> None:
        self.name, self.arguments, self.durable, self._log = name, arguments, durable, log
        self.binds: list[tuple[str, str | None]] = []
        self.callback = None

    async def bind(self, exchange, routing_key=None):
        self._log.append(("bind", self.name))
        self.binds.append((exchange.name, routing_key))

    async def consume(self, callback):
        self._log.append(("consume", self.name))
        self.callback = callback
        return "tag"


class FakeExchange:
    def __init__(self, name: str, type_, durable, arguments, extra=None) -> None:
        self.name, self.type, self.durable, self.arguments = name, type_, durable, arguments
        self.extra = extra or {}  # any other keyword arguments the code passed


class FakeChannel:
    def __init__(self) -> None:
        self.log: list[tuple[str, str]] = []
        self.exchanges: dict[str, FakeExchange] = {}
        self.queues: dict[str, FakeQueue] = {}
        self.prefetch: int | None = None
        self.closed = False

    async def set_qos(self, prefetch_count: int = 0):
        self.prefetch = prefetch_count

    async def declare_exchange(self, name, type_, durable=None, arguments=None, **kw):
        self.log.append(("exchange", name))
        self.exchanges[name] = FakeExchange(name, type_, durable, arguments, kw)
        return self.exchanges[name]

    async def declare_queue(self, name, durable=None, arguments=None, **kw):
        self.log.append(("queue", name))
        self.queues[name] = FakeQueue(name, self.log, arguments, durable)
        return self.queues[name]

    async def close(self):
        self.closed = True


class FakeCallbacks:
    def __init__(self) -> None:
        self.items: list = []

    def add(self, cb):
        self.items.append(cb)


class FakeConnection:
    def __init__(self, channel: FakeChannel) -> None:
        self._channel = channel
        self.close_callbacks = FakeCallbacks()
        self.reconnect_callbacks = FakeCallbacks()
        self.closed = False

    async def channel(self):
        return self._channel

    async def close(self):
        self.closed = True
