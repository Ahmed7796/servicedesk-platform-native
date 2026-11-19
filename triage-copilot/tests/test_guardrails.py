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
