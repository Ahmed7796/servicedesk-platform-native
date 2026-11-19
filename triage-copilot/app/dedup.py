"""Processed-event store (CLAUDE.md rule 5). Keys: processed:copilot:{eventId}, attempts:copilot:{eventId}."""
from typing import Protocol

PROCESSED_TTL_SECONDS = 7 * 24 * 3600  # longer than any outbox retry or broker outage; same as notify
ATTEMPTS_TTL_SECONDS = 3600  # the retry counter only needs to outlive the 1-2 s backoffs


class ProcessedStore(Protocol):
    async def is_processed(self, event_id: str) -> bool: ...
    async def mark_processed(self, event_id: str) -> None: ...
    async def incr_attempts(self, event_id: str) -> int: ...
    async def clear_attempts(self, event_id: str) -> None: ...


class RedisProcessedStore:
    """`client` is a redis.asyncio.Redis created with decode_responses=True. Errors are not caught here."""

    def __init__(self, client) -> None:
        self._r = client

    async def is_processed(self, event_id: str) -> bool:
        return bool(await self._r.exists(f"processed:copilot:{event_id}"))

    async def mark_processed(self, event_id: str) -> None:
        await self._r.set(f"processed:copilot:{event_id}", "1", nx=True, ex=PROCESSED_TTL_SECONDS)

    async def incr_attempts(self, event_id: str) -> int:
        key = f"attempts:copilot:{event_id}"
        async with self._r.pipeline(transaction=True) as pipe:
            pipe.incr(key)
            pipe.expire(key, ATTEMPTS_TTL_SECONDS)
            count, _ = await pipe.execute()
        return int(count)

    async def clear_attempts(self, event_id: str) -> None:
        await self._r.delete(f"attempts:copilot:{event_id}")
