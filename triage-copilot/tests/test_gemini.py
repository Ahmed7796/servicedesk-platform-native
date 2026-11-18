import asyncio
import json
import logging

import httpx
import pytest
from google.genai import errors
from pydantic import SecretStr

from app.config import Settings
from app.errors import LlmRetryableError
from app.gemini import GeminiClient, build_llm, sanitize_schema
from app.llm import ToolCall, Turn
from app.models import TriageSuggestionInput
from app.tools import TOOL_SPECS
from tests.fake_genai import FakeGenAI

pytestmark = pytest.mark.anyio

KEY = "gk-SECRET-KEY-123"
PROMPT = "PROMPT-SENTINEL"
REPLY = "REPLY-SENTINEL"
LEAK = "LEAK-SENTINEL"
RID = "11111111-1111-1111-1111-111111111111"
SCHEMA = TriageSuggestionInput.model_json_schema(by_alias=True)


def adapter(fake) -> GeminiClient:
    return GeminiClient(SecretStr(KEY), "gemini-test", client=fake)


def api_error(code):
    return errors.APIError(code, {"error": {"message": LEAK, "status": "X"}})


def walk(node):
    """Every dict in a schema tree."""
    if isinstance(node, dict):
        yield node
        for v in node.values():
            yield from walk(v)
    elif isinstance(node, list):
        for v in node:
            yield from walk(v)


# ---- step

async def test_step_declares_exactly_the_read_tools_and_disables_auto_calls():
    fake = FakeGenAI(calls=[("get_request", {"request_id": RID}), ("get_requester_history", {"requester_id": RID})])
    reply = await adapter(fake).step("SYS", [Turn("user", "go")], TOOL_SPECS)
    cfg = fake.requests[0]["config"]
    names = [d.name for t in cfg.tools for d in t.function_declarations]
    assert names == ["get_request", "get_requester_history"]  # rule 6: no write tool can be declared
    assert cfg.automatic_function_calling.disable is True
    assert cfg.system_instruction == "SYS" and fake.requests[0]["model"] == "gemini-test"
    assert reply.tool_calls == [ToolCall("get_request", {"request_id": RID}),
                                ToolCall("get_requester_history", {"requester_id": RID})]


async def test_step_text_only_reply_has_no_calls():
    assert (await adapter(FakeGenAI(text="ready")).step("S", [Turn("user", "x")], TOOL_SPECS)).tool_calls == []


async def test_step_sends_sanitised_tool_parameters():
    fake = FakeGenAI()
    await adapter(fake).step("S", [Turn("user", "x")], TOOL_SPECS)
    for decl in (d for t in fake.requests[0]["config"].tools for d in t.function_declarations):
        assert not {"additionalProperties", "format", "default"} & {k for n in walk(decl.parameters_json_schema) for k in n}
        assert decl.parameters_json_schema["required"]


async def test_history_maps_to_roles_and_parts():
    fake = FakeGenAI(text="x")
    history = [Turn("user", "hello"),
               Turn("model", tool_calls=(ToolCall("get_request", {"request_id": RID}),)),
               Turn("tool", tool_results=(("get_request", "WRAPPED"),))]
    await adapter(fake).step("S", history, TOOL_SPECS)
    u, m, t = fake.requests[0]["contents"]
    assert (u.role, m.role, t.role) == ("user", "model", "user")
    assert u.parts[0].text == "hello"
    assert m.parts[0].function_call.name == "get_request" and m.parts[0].function_call.args == {"request_id": RID}
    fr = t.parts[0].function_response
    assert fr.name == "get_request" and fr.response == {"result": "WRAPPED"}


# ---- final

async def test_final_has_no_tools_asks_for_json_and_returns_text():
    fake = FakeGenAI(text='{"a": 1}')
    out = await adapter(fake).final("SYS", [Turn("user", "x")], SCHEMA)
    cfg = fake.requests[0]["config"]
    assert out == '{"a": 1}' and not cfg.tools
    assert cfg.response_mime_type == "application/json"
    assert cfg.response_json_schema == sanitize_schema(SCHEMA)


@pytest.mark.parametrize("text", [None, "", "   "])
async def test_final_empty_text_is_a_plain_non_retryable_error(text):
    with pytest.raises(ValueError, match="empty response") as ei:
        await adapter(FakeGenAI(text=text)).final("S", [Turn("user", "x")], SCHEMA)
    assert not isinstance(ei.value, LlmRetryableError)


# ---- sanitiser

def test_sanitiser_removes_unsupported_keywords_everywhere():
    out = sanitize_schema(SCHEMA)
    banned = {"additionalProperties", "$defs", "$ref", "title", "format", "default"}
    assert all(k not in banned for n in walk(out) for k in n)
    assert "additionalProperties" in SCHEMA  # the input really had one, so the test is not vacuous


def test_sanitiser_keeps_the_constraints_and_does_not_mutate_input():
    before = json.dumps(SCHEMA, sort_keys=True)
    out = sanitize_schema(SCHEMA)
    assert json.dumps(SCHEMA, sort_keys=True) == before
    assert set(out["required"]) == set(SCHEMA["required"])
    props = out["properties"]
    assert props["priority"]["enum"] == SCHEMA["properties"]["priority"]["enum"]
    route = props["suggestedRoute"]
    assert (route["minItems"], route["maxItems"]) == (1, 5)
    assert route["items"]["pattern"] == SCHEMA["properties"]["suggestedRoute"]["items"]["pattern"]
    assert props["confidence"]["minimum"] == 0 and props["confidence"]["maximum"] == 1


def test_sanitiser_keeps_a_property_named_like_a_banned_keyword_and_inlines_refs():
    schema = {"type": "object", "$defs": {"P": {"type": "string", "title": "T"}},
              "properties": {"title": {"$ref": "#/$defs/P"}, "default": {"type": "integer", "default": 3}},
              "required": ["title"], "additionalProperties": False}
    assert sanitize_schema(schema) == {"type": "object", "properties": {
        "title": {"type": "string"}, "default": {"type": "integer"}}, "required": ["title"]}


# ---- error mapping

@pytest.mark.parametrize("code", [429, 500, 502, 503])
async def test_retryable_api_codes(code):
    with pytest.raises(LlmRetryableError):
        await adapter(FakeGenAI(error=api_error(code))).final("S", [Turn("user", "x")], SCHEMA)


@pytest.mark.parametrize("code", [400, 401, 403, 404])
async def test_other_client_errors_are_not_retryable(code):
    with pytest.raises(errors.APIError) as ei:
        await adapter(FakeGenAI(error=api_error(code))).step("S", [Turn("user", "x")], TOOL_SPECS)
    assert not isinstance(ei.value, LlmRetryableError)


_httpx2_error = type("ConnectError", (Exception,), {"__module__": "httpx2"})


@pytest.mark.parametrize("exc", [httpx.ConnectError("boom"), httpx.ReadTimeout("slow"), _httpx2_error("x"),
                                 TimeoutError(), ConnectionResetError()])
async def test_network_and_timeout_errors_are_retryable(exc):
    with pytest.raises(LlmRetryableError):
        await adapter(FakeGenAI(error=exc)).step("S", [Turn("user", "x")], TOOL_SPECS)


async def test_unrelated_errors_pass_through():
    with pytest.raises(KeyError):
        await adapter(FakeGenAI(error=KeyError("bug"))).final("S", [Turn("user", "x")], SCHEMA)


async def test_cancellation_is_never_swallowed():
    with pytest.raises(asyncio.CancelledError):
        await adapter(FakeGenAI(error=asyncio.CancelledError())).final("S", [Turn("user", "x")], SCHEMA)


async def test_retryable_error_hides_the_cause_and_the_message_text():
    with pytest.raises(LlmRetryableError) as ei:
        await adapter(FakeGenAI(error=api_error(503))).final("S", [Turn("user", "x")], SCHEMA)
    assert ei.value.__cause__ is None and ei.value.__suppress_context__ is True
    assert LEAK not in str(ei.value) and "503" in str(ei.value)


# ---- logs, repr, factory

async def test_no_key_prompt_or_reply_text_in_logs_or_repr(caplog):
    caplog.set_level(logging.DEBUG)
    ok = FakeGenAI(text=REPLY, calls=[("get_request", {"request_id": RID})])
    a = adapter(ok)
    await a.step("S", [Turn("user", PROMPT)], TOOL_SPECS)
    await a.final("S", [Turn("user", PROMPT), Turn("tool", tool_results=(("get_request", PROMPT),))], SCHEMA)
    for err in (api_error(429), api_error(400), httpx.ConnectError(LEAK)):
        with pytest.raises(Exception):
            await adapter(FakeGenAI(error=err)).final("S", [Turn("user", PROMPT)], SCHEMA)
    for secret in (KEY, PROMPT, REPLY, LEAK):
        assert secret not in caplog.text and secret not in repr(a)


def test_build_llm_needs_a_key():
    base = dict(_env_file=None, servicedesk_api_url="http://x", triage_client_id="t", triage_client_secret="s")
    assert build_llm(Settings(**base)) is None
    built = build_llm(Settings(**base, gemini_api_key=KEY, gemini_model="m-1"))
    assert isinstance(built, GeminiClient) and KEY not in repr(built)


@pytest.mark.parametrize("raw", ["", "   ", "\t\n"])
def test_empty_or_blank_key_means_no_key(raw):
    base = dict(_env_file=None, servicedesk_api_url="http://x", triage_client_id="t", triage_client_secret="s")
    s = Settings(**base, gemini_api_key=raw)
    assert s.gemini_api_key is None and build_llm(s) is None


def test_real_key_is_kept_secret():
    base = dict(_env_file=None, servicedesk_api_url="http://x", triage_client_id="t", triage_client_secret="s")
    assert Settings(**base, gemini_api_key=KEY).gemini_api_key.get_secret_value() == KEY
