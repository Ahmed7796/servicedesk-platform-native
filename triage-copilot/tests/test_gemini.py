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


# ---- thought signatures (M3-3b-fix)

SIG = b"SIG-SENTINEL-bytes"


async def test_signature_is_echoed_unchanged_on_the_same_part():
    fake = FakeGenAI(text="{}", calls=[("get_request", {"request_id": RID}, SIG)])
    llm = adapter(fake)
    reply = await llm.step("S", [Turn("user", "x")], TOOL_SPECS)
    history = [Turn("user", "x"), Turn("model", tool_calls=tuple(reply.tool_calls)),
               Turn("tool", tool_results=(("get_request", "W"),))]
    await llm.final("S", history, SCHEMA)
    model_content = fake.requests[1]["contents"][1]
    assert model_content.role == "model"
    assert model_content.parts[0].function_call.name == "get_request"
    assert model_content.parts[0].thought_signature == SIG


async def test_parallel_calls_keep_each_signature_on_its_own_part_and_text_part_is_ignored():
    fake = FakeGenAI(text="{}", lead_text="thinking", calls=[("get_request", {"request_id": RID}, SIG),
                                                  ("get_requester_history", {"requester_id": RID}),
                                                  ("get_request", {"request_id": RID}, b"two")])
    llm = adapter(fake)
    reply = await llm.step("S", [Turn("user", "x")], TOOL_SPECS)
    assert [c.name for c in reply.tool_calls] == ["get_request", "get_requester_history", "get_request"]
    await llm.final("S", [Turn("user", "x"), Turn("model", tool_calls=tuple(reply.tool_calls))], SCHEMA)
    parts = fake.requests[1]["contents"][1].parts
    assert [p.thought_signature for p in parts] == [SIG, None, b"two"]


async def test_no_signature_sends_none():
    fake = FakeGenAI(text="{}", calls=[("get_request", {"request_id": RID})])
    llm = adapter(fake)
    reply = await llm.step("S", [Turn("user", "x")], TOOL_SPECS)
    await llm.final("S", [Turn("user", "x"), Turn("model", tool_calls=tuple(reply.tool_calls))], SCHEMA)
    assert fake.requests[1]["contents"][1].parts[0].thought_signature is None


async def test_signature_never_appears_in_repr_logs_or_serialisation(caplog):
    caplog.set_level(logging.DEBUG)
    fake = FakeGenAI(text="{}", calls=[("get_request", {"request_id": RID}, SIG)])
    reply = await adapter(fake).step("S", [Turn("user", "x")], TOOL_SPECS)
    call = reply.tool_calls[0]
    turn = Turn("model", tool_calls=(call,))
    assert call.state is not None
    assert "SIG-SENTINEL" not in repr(call) + repr(turn) + repr(call.state) + str(call.state)
    assert "SIG-SENTINEL" not in caplog.text
    import copy
    import pickle

    from pydantic import TypeAdapter
    for dump in (json.dumps, lambda o: TypeAdapter(object).dump_json(o), pickle.dumps, copy.deepcopy):
        with pytest.raises(Exception):
            dump(call.state)


def test_toolcall_equality_ignores_the_opaque_state():
    from app.llm import Opaque
    assert ToolCall("a", {}, Opaque(b"1")) == ToolCall("a", {})


def test_default_model_and_sdk_timeout_below_the_agent_bound(monkeypatch):
    seen = {}
    monkeypatch.setattr("app.gemini.genai.Client", lambda **kw: seen.update(kw) or object())
    base = dict(_env_file=None, servicedesk_api_url="http://x", triage_client_id="t", triage_client_secret="s")
    s = Settings(**base, gemini_api_key=KEY)
    assert s.gemini_model == "gemini-3.5-flash-lite"
    build_llm(s)
    opts = seen["http_options"]
    assert opts.timeout == 15000  # milliseconds in google-genai 3.5.0
    assert opts.timeout / 1000 < s.llm_timeout_seconds
    assert opts.retry_options is None  # SDK default is never retry; the bounded retry is M3-3c's


async def test_blocked_reply_with_no_content_is_an_empty_reply():
    from types import SimpleNamespace
    fake = FakeGenAI(text="")
    async def gen(**kw):
        return SimpleNamespace(text=None, function_calls=None, candidates=[SimpleNamespace(content=None)])
    fake.aio.models.generate_content = lambda **kw: gen(**kw)
    assert (await adapter(fake).step("S", [Turn("user", "x")], TOOL_SPECS)).tool_calls == []


def test_aiohttp_is_not_installed():
    # The SDK's aiohttp transport retries connection errors even with retry_options=None, which would break
    # "M3-3c's bounded retry is the only retry". If this fails, re-check the SDK's retry behaviour first.
    import importlib.util
    assert importlib.util.find_spec("aiohttp") is None
