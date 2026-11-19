import json
import logging
from pathlib import Path

import pytest

from app.agent import TriageOutcome
from app.handler import Action, Deps, MAX_ATTEMPTS, handle
from tests.fake_store import FakeStore

pytestmark = pytest.mark.anyio
EXAMPLE = json.loads((Path(__file__).resolve().parents[2] / "contracts/events/examples/request.submitted.json").read_text())
EID, RID = EXAMPLE["eventId"], EXAMPLE["requestId"]


def body(**over) -> bytes:
    return json.dumps({**EXAMPLE, **over}).encode()


class Triage:
    def __init__(self, store: FakeStore, *outcomes, exc: Exception | None = None):
        self.store, self.outcomes, self.exc, self.seen = store, list(outcomes), exc, []

    async def __call__(self, request_id: str) -> TriageOutcome:
        self.seen.append((request_id, list(self.store.calls)))
        if self.exc:
            raise self.exc
        return self.outcomes.pop(0) if len(self.outcomes) > 1 else self.outcomes[0]


POSTED = TriageOutcome("posted")
RETRY = TriageOutcome("failed", "ServiceDesk API unavailable", retryable=True)
FATAL = TriageOutcome("failed", "ApiForbiddenError")
SKIPPED = TriageOutcome("skipped", "no key, skipping")


def setup(*outcomes, exc=None):
    store = FakeStore()
    t = Triage(store, *outcomes, exc=exc)
    return store, t, Deps(store=store, triage=t)


async def test_posted_is_marked_after_triage_and_acked():
    store, t, deps = setup(POSTED)
    d = await handle(body(), deps)
    assert d.action is Action.ACK and EID in store.processed
    assert t.seen[0][0] == RID
    names = [n for n, _ in store.calls]
    assert names.index("is_processed") < names.index("incr_attempts") < names.index("mark_processed")
    assert "mark_processed" not in [n for n, _ in t.seen[0][1]]  # not marked before the work ran
    assert store.attempts == {}


async def test_duplicate_event_is_skipped():
    store, t, deps = setup(POSTED)
    store.processed.add(EID)
    d = await handle(body(), deps)
    assert d.action is Action.ACK and t.seen == []
    assert ("mark_processed", EID) not in store.calls and ("incr_attempts", EID) not in store.calls


@pytest.mark.parametrize("outcome", [POSTED, SKIPPED, FATAL], ids=["posted", "skipped", "nonretryable"])
async def test_terminal_outcomes_are_marked_and_acked(outcome):
    store, t, deps = setup(outcome)
    assert (await handle(body(), deps)).action is Action.ACK
    assert EID in store.processed


async def test_retryable_twice_then_dead_letter_on_third_attempt(caplog):
    store, t, deps = setup(RETRY)
    d1 = await handle(body(), deps)
    d2 = await handle(body(), deps)
    assert (d1.action, d2.action) == (Action.RETRY, Action.RETRY)
    assert 0 < d1.delay_seconds < d2.delay_seconds
    assert EID not in store.processed
    with caplog.at_level(logging.WARNING):
        d3 = await handle(body(), deps)
    assert d3.action is Action.DEAD_LETTER and MAX_ATTEMPTS == 3 and len(t.seen) == 3
    assert EID not in store.processed
    assert store.attempts == {}  # cleared so a replay from the DLQ starts fresh
    exhausted = [r for r in caplog.records if "retries exhausted" in r.getMessage()]
    assert len(exhausted) == 1
    assert RID in exhausted[0].getMessage() and "ServiceDesk API unavailable" in exhausted[0].getMessage()


async def test_retry_then_success_is_marked():
    store, t, deps = setup(RETRY, POSTED)
    assert (await handle(body(), deps)).action is Action.RETRY
    assert (await handle(body(), deps)).action is Action.ACK
    assert EID in store.processed and store.attempts == {}


async def test_nonretryable_is_acked_and_nothing_is_retried():
    store, t, deps = setup(FATAL)
    assert (await handle(body(), deps)).action is Action.ACK
    assert len(t.seen) == 1


@pytest.mark.parametrize("raw", [b"junk", b"[]", json.dumps({"eventId": "x"}).encode(), body(requestId="nope")])
async def test_bad_envelope_goes_straight_to_dlq(raw):
    store, t, deps = setup(POSTED)
    d = await handle(raw, deps)
    assert d.action is Action.DEAD_LETTER and t.seen == [] and store.calls == []


@pytest.mark.parametrize("over", [{"type": "request.approved"}, {"version": 2}])
async def test_unsupported_type_or_version_is_acked_unmarked(over):
    store, t, deps = setup(POSTED)
    d = await handle(body(**over), deps)
    assert d.action is Action.ACK and t.seen == [] and store.processed == set()
    assert not any(n == "incr_attempts" for n, _ in store.calls)


async def test_no_llm_means_skip_marked_and_acked():
    store = FakeStore()
    d = await handle(body(), Deps(store=store, triage=None))
    assert d.action is Action.ACK and EID in store.processed


async def test_redis_down_on_check_retries_and_triages_nothing():
    store, t, deps = setup(POSTED)
    store.fail_on = {"is_processed"}
    d = await handle(body(), deps)
    assert d.action is Action.RETRY and t.seen == [] and d.delay_seconds > 0


async def test_redis_down_on_mark_after_post_retries_not_acked():
    store, t, deps = setup(POSTED)
    store.fail_on = {"mark_processed"}
    assert (await handle(body(), deps)).action is Action.RETRY
    assert EID not in store.processed


async def test_mark_failure_on_last_attempt_dead_letters():
    store, t, deps = setup(POSTED)
    store.attempts[EID] = MAX_ATTEMPTS - 1
    store.fail_on = {"mark_processed"}
    assert (await handle(body(), deps)).action is Action.DEAD_LETTER


async def test_unexpected_triage_exception_is_treated_as_retryable():
    store, t, deps = setup(exc=RuntimeError("boom SENTINEL"))
    assert (await handle(body(), deps)).action is Action.RETRY
    assert EID not in store.processed


async def test_cancellation_is_not_swallowed():
    import asyncio
    store, t, deps = setup(exc=asyncio.CancelledError())
    with pytest.raises(asyncio.CancelledError):
        await handle(body(), deps)


async def test_logs_carry_no_request_text_and_no_exception_message(caplog):
    secret_title = "SENTINEL-TITLE-text"
    raw = body(payload={**EXAMPLE["payload"], "title": secret_title})
    cases = []
    s1, _, d1 = setup(exc=RuntimeError("SENTINEL-EXC"))
    s2, _, d2 = setup(POSTED); s2.fail_on = {"is_processed"}
    s3, _, d3 = setup(RETRY); s3.attempts[EID] = MAX_ATTEMPTS - 1
    with caplog.at_level(logging.DEBUG):
        for deps in (d1, d2, d3):
            await handle(raw, deps)
        await handle(b"SENTINEL-BODY not json", d1)
        await handle(body(eventId="SENTINEL-BAD"), d1)
    text = "\n".join(r.getMessage() + repr(r.args) + str(r.exc_info) for r in caplog.records)
    assert caplog.records
    for needle in ("SENTINEL", "hunter2", "boom"):
        assert needle not in text
    assert all(r.exc_info is None for r in caplog.records)
    assert "ConnectionError" in text  # class name is logged


async def test_stub_accepts_a_second_post_upsert_itself_rests_on_m1_4b(client, stub):
    # Only shows the client copes with two posts. That the API replaces the suggestion is M1-4b's acceptance.
    import httpx
    from app.models import TriageSuggestionInput
    from tests.test_agent import GOOD, RID as R, STORED, TRIAGE
    stub.queue("POST", TRIAGE, httpx.Response(200, json=STORED), httpx.Response(200, json=STORED))
    s = TriageSuggestionInput.model_validate(GOOD)
    assert await client.post_triage(R, s) and await client.post_triage(R, s)
    assert stub.count("POST", TRIAGE) == 2


async def test_incr_attempts_failure_retries_and_triages_nothing():
    store, t, deps = setup(POSTED)
    store.fail_on = {"incr_attempts"}
    d = await handle(body(), deps)
    assert d.action is Action.RETRY and t.seen == [] and EID not in store.processed


async def test_clear_attempts_failure_after_mark_is_best_effort(caplog):
    store, t, deps = setup(POSTED)
    store.attempts[EID] = MAX_ATTEMPTS - 1
    store.fail_on = {"clear_attempts"}
    with caplog.at_level(logging.DEBUG):
        d = await handle(body(), deps)
    assert d.action is Action.ACK and EID in store.processed
    text = "\n".join(r.getMessage() for r in caplog.records)
    assert "exhausted" not in text and "hunter2" not in text and "ConnectionError" in text


async def test_retry_logs_the_attempt_number_without_text(caplog):
    store, t, deps = setup(RETRY)
    with caplog.at_level(logging.INFO):
        await handle(body(), deps)
    msg = [r.getMessage() for r in caplog.records if "attempt" in r.getMessage()]
    assert msg == [f"triage attempt 1/{MAX_ATTEMPTS} failed for request {RID}: ServiceDesk API unavailable, retrying"]
