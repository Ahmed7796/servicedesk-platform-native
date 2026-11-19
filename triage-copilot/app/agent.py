"""triage(request_id): run read tools through our dispatcher, then post one validated suggestion.

The model can only ask for the two read tools. The final answer comes from a separate call with no tools,
is validated here, and is posted by our code. The post is never a tool (CLAUDE.md, rule 6).
"""
import asyncio
import logging
from dataclasses import dataclass
from typing import Literal
from uuid import UUID

from pydantic import ValidationError

from app.config import Settings
from app.errors import ApiForbiddenError, ApiResponseError, ApiUnavailableError, LlmRetryableError, ServiceAuthError
from app.llm import LlmClient, Turn
from app.models import TriageSuggestionInput
from app.prompt import SYSTEM, user_prompt, wrap_data
from app.ratelimit import RateLimiter
from app.servicedesk_client import ServiceDeskClient
from app.tools import TOOL_SPECS, TriageTools

log = logging.getLogger("app.agent")
MAX_CALLS_PER_ROUND = 4
OUTPUT_FIELDS = {"category", "priority", "suggestedRoute", "summary", "confidence"}


class _LlmCallTimeout(Exception):
    """One model call took longer than llm_timeout_seconds."""


@dataclass(frozen=True)
class TriageOutcome:
    status: Literal["posted", "skipped", "failed", "timed_out"]
    reason: str = ""
    retryable: bool = False  # only transient API or time problems; odd model output is never retried here


def _outcome(request_id: str | None, status: str, reason: str, retryable: bool = False) -> TriageOutcome:
    # Only a validated UUID or nothing, and fixed reason strings: no model or request text in the logs.
    log.warning("triage %s for request %s: %s", status, request_id or "(unknown)", reason)
    return TriageOutcome(status, reason, retryable)  # type: ignore[arg-type]


async def triage(request_id: str, llm: LlmClient, tools: TriageTools, client: ServiceDeskClient,
                 settings: Settings, limiter: RateLimiter | None = None) -> TriageOutcome:
    try:
        request_id = str(UUID(request_id))
    except (ValueError, TypeError, AttributeError):
        return _outcome(None, "failed", "invalid request id")
    if settings.copilot_mode == "llm" and settings.gemini_api_key is None:  # canned mode needs no key
        return _outcome(request_id, "skipped", "no key, skipping")
    if limiter is not None and settings.copilot_mode == "llm" and not limiter.try_acquire():  # canned: no LLM call
        return _outcome(request_id, "skipped", "rate limited, nothing posted")
    try:
        async with asyncio.timeout(settings.triage_total_timeout_seconds):
            raw = await _converse(request_id, llm, tools, settings)
            try:
                suggestion = TriageSuggestionInput.model_validate_json(raw)
            except ValidationError as exc:
                return _outcome(request_id, "failed", f"invalid model output, nothing posted ({_describe(exc)})")
            if suggestion.confidence < settings.copilot_min_confidence:
                return _outcome(request_id, "skipped", "low confidence, nothing posted")
            stored = await client.post_triage(request_id, suggestion)
    except _LlmCallTimeout:
        return _outcome(request_id, "timed_out", "llm call timeout", retryable=True)
    except TimeoutError:
        return _outcome(request_id, "timed_out", "overall timeout", retryable=True)
    except LlmRetryableError:
        return _outcome(request_id, "failed", "llm unavailable", retryable=True)
    except ApiUnavailableError:
        return _outcome(request_id, "failed", "ServiceDesk API unavailable", retryable=True)
    except (ApiForbiddenError, ServiceAuthError, ApiResponseError) as exc:
        return _outcome(request_id, "failed", type(exc).__name__)
    except Exception as exc:  # model or SDK failure: advisory, so log the class and post nothing
        return _outcome(request_id, "failed", f"unexpected {type(exc).__name__}")
    if stored is None:
        return _outcome(request_id, "failed", "request not found when posting")
    canned = " (canned demo)" if settings.copilot_mode == "canned" else ""
    log.info("triage posted%s for request %s", canned, request_id)  # id only: no request or model text
    return TriageOutcome("posted")


async def _converse(request_id: str, llm: LlmClient, tools: TriageTools, settings: Settings) -> str:
    history = [Turn("user", user_prompt(request_id))]
    for _ in range(settings.triage_max_tool_rounds):
        reply = await _bounded(llm.step(SYSTEM, history, TOOL_SPECS), settings.llm_timeout_seconds)
        if not reply.tool_calls:
            break
        calls = tuple(reply.tool_calls[:MAX_CALLS_PER_ROUND])
        results = []
        for call in calls:
            results.append((call.name, wrap_data(await tools.call(call.name, call.args))))
        history += [Turn("model", tool_calls=calls), Turn("tool", tool_results=tuple(results))]
    schema = TriageSuggestionInput.model_json_schema(by_alias=True)
    return await _bounded(llm.final(SYSTEM, history, schema), settings.llm_timeout_seconds)


async def _bounded(call, seconds: float):
    try:
        return await asyncio.wait_for(call, seconds)
    except TimeoutError:
        raise _LlmCallTimeout from None  # the overall timeout cancels instead, so it does not land here


def _describe(exc: ValidationError) -> str:
    """Names of known fields and error types only. A model-chosen key (an extra field) is never repeated."""
    parts = set()
    for e in exc.errors():
        first = e["loc"][0] if e["loc"] else None
        name = first if first in OUTPUT_FIELDS else ("(root)" if first is None else "(extra field)")
        parts.add(f"{name}: {e['type']}")
    return "; ".join(sorted(parts))
