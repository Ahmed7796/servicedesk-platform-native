import os
import uuid

import pytest

from app.dedup import ATTEMPTS_TTL_SECONDS, PROCESSED_TTL_SECONDS, RedisProcessedStore

pytestmark = pytest.mark.anyio
URL = os.environ.get("REDIS_TEST_URL")
needs_redis = pytest.mark.skipif(not URL, reason="REDIS_TEST_URL is not set (see the M3-3c-1 hand-off for the command)")


def test_ttls():
    assert PROCESSED_TTL_SECONDS == 7 * 24 * 3600 and ATTEMPTS_TTL_SECONDS == 3600


@pytest.fixture
async def redis_client():
    import redis.asyncio as redis
    c = redis.from_url(URL, decode_responses=True)
    yield c
    await c.aclose()


@needs_redis
async def test_mark_sets_namespaced_key_with_ttl(redis_client):
    store, eid = RedisProcessedStore(redis_client), str(uuid.uuid4())
    assert not await store.is_processed(eid)
    await store.mark_processed(eid)
    await store.mark_processed(eid)  # harmless twice
    assert await store.is_processed(eid)
    assert await redis_client.exists(f"processed:copilot:{eid}") == 1
    ttl = await redis_client.ttl(f"processed:copilot:{eid}")
    assert PROCESSED_TTL_SECONDS - 60 < ttl <= PROCESSED_TTL_SECONDS


@needs_redis
async def test_mark_does_not_reset_an_existing_ttl(redis_client):
    store, eid = RedisProcessedStore(redis_client), str(uuid.uuid4())
    await redis_client.set(f"processed:copilot:{eid}", "1", ex=100)
    await store.mark_processed(eid)  # SET NX: leaves the key alone
    assert await redis_client.ttl(f"processed:copilot:{eid}") <= 100


@needs_redis
async def test_attempt_counter_increments_expires_and_clears(redis_client):
    store, eid = RedisProcessedStore(redis_client), str(uuid.uuid4())
    assert [await store.incr_attempts(eid) for _ in range(3)] == [1, 2, 3]
    ttl = await redis_client.ttl(f"attempts:copilot:{eid}")
    assert 0 < ttl <= ATTEMPTS_TTL_SECONDS
    await store.clear_attempts(eid)
    assert await store.incr_attempts(eid) == 1
    await store.clear_attempts(eid)
