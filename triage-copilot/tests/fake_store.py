class FakeStore:
    """In-memory ProcessedStore that records the order of calls."""

    def __init__(self) -> None:
        self.processed: set[str] = set()
        self.attempts: dict[str, int] = {}
        self.calls: list[tuple[str, str]] = []
        self.fail_on: set[str] = set()  # method names that raise ConnectionError

    def _do(self, name: str, event_id: str) -> None:
        self.calls.append((name, event_id))
        if name in self.fail_on:
            raise ConnectionError("redis password=hunter2 is down")

    async def is_processed(self, event_id: str) -> bool:
        self._do("is_processed", event_id)
        return event_id in self.processed

    async def mark_processed(self, event_id: str) -> None:
        self._do("mark_processed", event_id)
        self.processed.add(event_id)

    async def incr_attempts(self, event_id: str) -> int:
        self._do("incr_attempts", event_id)
        self.attempts[event_id] = self.attempts.get(event_id, 0) + 1
        return self.attempts[event_id]

    async def clear_attempts(self, event_id: str) -> None:
        self._do("clear_attempts", event_id)
        self.attempts.pop(event_id, None)
