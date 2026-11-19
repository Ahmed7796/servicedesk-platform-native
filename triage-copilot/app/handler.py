"""Decide what to do with one request.submitted message. No broker types: the consumer (M3-3c-2) acts on Decision.

Order: parse, check processed, count the attempt, triage, mark only if the outcome is terminal.
Logs hold event and request ids (validated UUIDs), fixed reason strings and exception class names. Never the
body, request text or str(exc).
"""
import logging
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from enum import Enum

from app.agent import TriageOutcome
from app.dedup import ProcessedStore
from app.events import EnvelopeError, parse_envelope

log = logging.getLogger("app.handler")
MAX_ATTEMPTS = 3
BACKOFF_SECONDS = (1.0, 2.0)  # after attempt 1 and attempt 2
SUPPORTED_TYPE, SUPPORTED_VERSION = "request.submitted", 1


class Action(Enum):
    ACK = "ack"
    RETRY = "retry"  # requeue after delay_seconds
    DEAD_LETTER = "dead_letter"  # reject without requeue


@dataclass(frozen=True)
class Decision:
    action: Action
    delay_seconds: float = 0.0


@dataclass(frozen=True)
class Deps:
    store: ProcessedStore
    triage: Callable[[str], Awaitable[TriageOutcome]] | None  # None: no LLM, so triage is skipped


async def handle(body: bytes, deps: Deps) -> Decision:
    try:
        env = parse_envelope(body)
    except EnvelopeError:
        log.warning("invalid envelope, dead-lettered")
        return Decision(Action.DEAD_LETTER)
    if env.type != SUPPORTED_TYPE or env.version != SUPPORTED_VERSION:
        log.info("unsupported event %s v%s, skipped (event %s)", env.type, env.version, env.event_id)
        return Decision(Action.ACK)

    eid, rid = env.event_id, env.request_id
    try:
        if await deps.store.is_processed(eid):
            log.info("duplicate event %s, skipped", eid)
            return Decision(Action.ACK)
        attempt = await deps.store.incr_attempts(eid)
    except Exception as exc:  # Redis: never skip the check, so ask for a redelivery
        log.warning("store unavailable (%s) for event %s", type(exc).__name__, eid)
        return Decision(Action.RETRY, BACKOFF_SECONDS[0])

    if deps.triage is None:
        outcome = TriageOutcome("skipped", "no llm, skipping")
        log.warning("triage skipped for request %s: no llm", rid)
    else:
        try:
            outcome = await deps.triage(rid)
        except Exception as exc:  # agent.triage catches model errors itself; this is the last guard
            outcome = TriageOutcome("failed", f"unexpected {type(exc).__name__}", retryable=True)

    if outcome.retryable:
        return await _retry_or_dead_letter(deps, eid, rid, attempt, outcome.reason)
    try:
        await deps.store.mark_processed(eid)
    except Exception as exc:
        # The post (if any) already happened and is an upsert, so a redelivery is harmless.
        return await _retry_or_dead_letter(deps, eid, rid, attempt, f"store {type(exc).__name__}")
    try:
        await deps.store.clear_attempts(eid)
    except Exception as exc:  # marked already: best effort only, the counter expires in an hour
        log.warning("store unavailable (%s) clearing attempts for event %s", type(exc).__name__, eid)
    return Decision(Action.ACK)


async def _retry_or_dead_letter(deps: Deps, eid: str, rid: str, attempt: int, reason: str) -> Decision:
    if attempt < MAX_ATTEMPTS:
        return Decision(Action.RETRY, BACKOFF_SECONDS[min(attempt, len(BACKOFF_SECONDS)) - 1])
    log.warning("triage retries exhausted for request %s (event %s): %s", rid, eid, reason)
    try:
        await deps.store.clear_attempts(eid)  # a replay from the DLQ starts fresh
    except Exception as exc:
        log.warning("store unavailable (%s) for event %s", type(exc).__name__, eid)
    return Decision(Action.DEAD_LETTER)
