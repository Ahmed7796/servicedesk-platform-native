"""Output-validation gaps (M3-5a): nothing the model returns reaches the API unless it validates."""
import json
import pathlib
import re

import pytest

from tests.test_agent import GOOD, GET, KEY, TRIAGE, api_calls, run, tools  # noqa: F401 (fixture)
from app.llm import LlmReply
from tests.fake_llm import FakeLlm
from tests.test_tools import serve

pytestmark = pytest.mark.anyio

MISSING = {k: v for k, v in GOOD.items() if k != "summary"}


@pytest.mark.parametrize("final", [
    "", "{}", "null", "true", '"text"', "{", json.dumps(MISSING),
    json.dumps({**GOOD, "priority": None}), json.dumps({**GOOD, "category": None}),
    json.dumps({**GOOD, "suggestedRoute": "DepartmentHead"}), json.dumps({**GOOD, "suggestedRoute": [1, 2]}),
    json.dumps({**GOOD, "suggestedRoute": [None]}), json.dumps({**GOOD, "confidence": -0.1}),
    json.dumps({**GOOD, "confidence": None}), json.dumps({**GOOD, "summary": 5}),
    '{"category":"Equipment","priority":"High","suggestedRoute":["DepartmentHead"],"summary":"s","confidence":NaN}',
    '{"category":"Equipment","priority":"High","suggestedRoute":["DepartmentHead"],"summary":"s","confidence":Infinity}',
    json.dumps({**GOOD, "requestId": "11111111-1111-4111-8111-111111111111"}),  # server-owned field
    json.dumps({**GOOD, "status": "Approved"}),  # rule 6: no sneaking an action in
    "```json\n" + json.dumps(GOOD) + "\n```",
    json.dumps(GOOD) + " trailing",
])
async def test_invalid_output_never_posts(tools, client, stub, final):  # noqa: F811
    serve(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=final), tools, client)
    assert (out.status, out.retryable) == ("failed", False)
    assert ("POST", TRIAGE) not in api_calls(stub)


def test_post_triage_has_exactly_one_call_site():
    app_dir = pathlib.Path(__file__).resolve().parents[1] / "app"
    hits = {p.name: len(re.findall(r"\.post_triage\(", p.read_text())) for p in app_dir.glob("*.py")}
    assert {k: v for k, v in hits.items() if v} == {"agent.py": 1}


def _conf(c) -> str:
    return json.dumps({**GOOD, "confidence": c})


@pytest.mark.parametrize("c,posts", [(0.49, False), (0.5, True), (0.0, False), (1.0, True), (0.4999999, False)])
async def test_low_confidence_boundary_at_default(tools, client, stub, c, posts):  # noqa: F811
    from tests.test_agent import post_ok
    serve(stub)
    post_ok(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=_conf(c)), tools, client)
    assert (("POST", TRIAGE) in api_calls(stub)) is posts
    assert out.status == ("posted" if posts else "skipped")
    if not posts:
        assert (out.retryable, out.reason) == (False, "low confidence, nothing posted")


async def test_custom_threshold(tools, client, stub):  # noqa: F811
    serve(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=_conf(0.8)), tools, client,
                    copilot_min_confidence=0.9)
    assert out.status == "skipped" and ("POST", TRIAGE) not in api_calls(stub)


async def test_threshold_zero_posts_everything_valid(tools, client, stub):  # noqa: F811
    from tests.test_agent import post_ok
    serve(stub)
    post_ok(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=_conf(0.0)), tools, client,
                    copilot_min_confidence=0)
    assert out.status == "posted"


async def test_invalid_and_low_confidence_stays_failed(tools, client, stub):  # noqa: F811
    serve(stub)
    out = await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()],
                            final=json.dumps({**GOOD, "confidence": 0.1, "extra": 1})), tools, client)
    assert out.status == "failed"  # validation comes first


async def test_low_confidence_log_has_no_value_or_model_text(tools, client, stub, caplog):  # noqa: F811
    import logging
    caplog.set_level(logging.DEBUG)
    serve(stub)
    final = json.dumps({**GOOD, "confidence": 0.123456, "summary": "SUMMARY-SENTINEL"})
    await run(FakeLlm([LlmReply(tool_calls=[GET]), LlmReply()], final=final), tools, client)
    assert "low confidence, nothing posted" in caplog.text
    assert "0.123456" not in caplog.text and "SUMMARY-SENTINEL" not in caplog.text


async def test_canned_confidence_passes_the_default_threshold(client, stub):
    from app.canned import CONFIDENCE
    from app.config import Settings
    assert CONFIDENCE >= Settings(_env_file=None, servicedesk_api_url="u", triage_client_id="i",
                                  triage_client_secret="s").copilot_min_confidence


async def test_low_confidence_outcome_is_marked_and_acked_by_the_real_handler():
    from app.agent import TriageOutcome
    from app.handler import Action, handle
    from tests.test_handler import EID, body, setup
    store, t, deps = setup(TriageOutcome("skipped", "low confidence, nothing posted"))
    assert (await handle(body(), deps)).action is Action.ACK and EID in store.processed
