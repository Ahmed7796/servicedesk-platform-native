import json
import logging

import pytest

from app.agent import triage
from app.canned import CannedLlm
from app.llm import LlmReply
from app.ratelimit import RateLimiter
from tests.fake_llm import FakeLlm
from tests.test_agent import GET, GOOD, TRIAGE, api_calls, cfg, post_ok, run, tools  # noqa: F401 (fixture)
from tests.test_canned import cfg as canned_cfg, stored
from tests.test_tools import RID, serve

pytestmark = pytest.mark.anyio


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


def test_allows_n_then_blocks():
    c = Clock()
    lim = RateLimiter(3, clock=c)
    assert [lim.try_acquire() for _ in range(4)] == [True, True, True, False]


def test_window_is_sixty_seconds_boundary_exact():
    c = Clock()
    lim = RateLimiter(1, clock=c)
    assert lim.try_acquire()
    c.t += 59.9
    assert not lim.try_acquire()  # a refused call does not use up or extend anything
    c.t += 0.1
    assert lim.try_acquire()
    assert not lim.try_acquire()


def test_tokens_return_one_by_one():
    c = Clock()
    lim = RateLimiter(2, clock=c)
    assert lim.try_acquire()
    c.t += 30
    assert lim.try_acquire()
    assert not lim.try_acquire()
    c.t += 30  # the first is 60 s old, the second 30 s
    assert lim.try_acquire() and not lim.try_acquire()


@pytest.mark.parametrize("bad", [0, -1, 1001, True, 1.5])
def test_bad_limit_is_rejected(bad):
    with pytest.raises(ValueError):
        RateLimiter(bad)


async def test_limited_triage_skips_before_any_llm_call(tools, client, stub, caplog):  # noqa: F811
    caplog.set_level(logging.INFO)
    lim = RateLimiter(1, clock=Clock())
    assert lim.try_acquire()  # budget used up
    llm = FakeLlm(final=json.dumps(GOOD))
    out = await triage(RID, llm, tools, client, cfg(), limiter=lim)
    assert (out.status, out.retryable, out.reason) == ("skipped", False, "rate limited, nothing posted")
    assert llm.step_calls == 0 and llm.final_calls == 0 and stub.calls == []
    assert RID in caplog.text


async def test_one_token_per_triage_not_per_step(tools, client, stub):  # noqa: F811
    for _ in range(3):
        serve(stub)
    post_ok(stub)
    lim = RateLimiter(2, clock=Clock())
    llm = FakeLlm([LlmReply(tool_calls=[GET]), LlmReply(tool_calls=[GET]), LlmReply()], final=json.dumps(GOOD))
    out = await triage(RID, llm, tools, client, cfg(), limiter=lim)
    assert out.status == "posted" and llm.step_calls == 3
    assert lim.try_acquire() and not lim.try_acquire()  # exactly one of two was used


async def test_no_limiter_means_no_limit(tools, client, stub):  # noqa: F811
    serve(stub)
    post_ok(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=json.dumps(GOOD)), tools, client)
    assert out.status == "posted"


async def test_missing_key_does_not_take_a_token(tools, client, stub):  # noqa: F811
    lim = RateLimiter(1, clock=Clock())
    out = await triage(RID, FakeLlm(), tools, client, cfg().model_copy(update={"gemini_api_key": None}), limiter=lim)
    assert out.reason == "no key, skipping" and lim.try_acquire()


async def test_canned_mode_never_takes_a_token(tools, client, stub):  # noqa: F811
    serve(stub)
    stored(stub)
    lim = RateLimiter(1, clock=Clock())
    assert lim.try_acquire()  # exhausted
    out = await triage(RID, CannedLlm(), tools, client, canned_cfg(), limiter=lim)
    assert out.status == "posted"


async def test_limited_outcome_is_acked_and_marked_by_the_real_handler():
    from app.agent import TriageOutcome
    from app.handler import Action
    from tests.test_handler import EID, body, setup

    store, t, deps = setup(TriageOutcome("skipped", "rate limited, nothing posted"))
    from app.handler import handle
    d = await handle(body(), deps)
    assert d.action is Action.ACK and EID in store.processed and store.attempts == {}


@pytest.mark.parametrize("bad", ["../approve\nFAKE LOG", "not-a-uuid", 123, None])
async def test_invalid_request_id_takes_no_token(tools, client, stub, bad):  # noqa: F811
    lim = RateLimiter(1, clock=Clock())
    out = await triage(bad, FakeLlm(), tools, client, cfg(), limiter=lim)
    assert out.reason == "invalid request id" and stub.calls == []
    assert lim.try_acquire()  # the single token is still there
