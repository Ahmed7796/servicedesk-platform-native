"""Gemini adapter behind LlmClient (google-genai 3.5.0).

Advisory only: step() declares just the read tools it is given and turns automatic function calling off, so
nothing runs inside the SDK. final() offers no tools. This module does no logging, so no key, prompt or reply
text can reach a log from here.
"""
import copy

from google import genai
from google.genai import errors, types
from pydantic import SecretStr

from app.config import Settings
from app.errors import LlmRetryableError
from app.llm import LlmReply, ToolCall, Turn

# Keywords Gemini's schema mode may reject. Strict validation in agent.py / tools.py stays the real gate.
_DROP = {"additionalProperties", "$defs", "$ref", "title", "format", "default"}
_SCHEMA_LISTS = ("anyOf", "oneOf", "allOf")
_TRANSPORT_MODULES = {"httpx", "httpx2", "aiohttp"}  # the SDK 3.5.0 depends on httpx2; classify without importing


def sanitize_schema(schema: dict) -> dict:
    """A copy of a JSON schema without the keywords above. $refs are inlined first. Names under
    `properties` are data, not keywords, so a property called "title" or "default" survives."""
    defs = schema.get("$defs", {})

    def walk(node, depth=0):
        if depth > 20:
            raise ValueError("schema too deep or cyclic")
        if "$ref" in node:
            return walk(defs[node["$ref"].rsplit("/", 1)[-1]], depth + 1)
        out = {}
        for key, value in node.items():
            if key in _DROP:
                continue
            if key == "properties":
                out[key] = {name: walk(sub, depth + 1) for name, sub in value.items()}
            elif key == "items" and isinstance(value, dict):
                out[key] = walk(value, depth + 1)
            elif key in _SCHEMA_LISTS:
                out[key] = [walk(sub, depth + 1) for sub in value]
            else:
                out[key] = copy.deepcopy(value)
        return out

    return walk(schema)


def _is_transport_error(exc: Exception) -> bool:
    if isinstance(exc, (TimeoutError, OSError)):
        return True
    return any(c.__module__.split(".")[0] in _TRANSPORT_MODULES for c in type(exc).__mro__)


def _contents(history: list[Turn]) -> list[types.Content]:
    out = []
    for turn in history:
        if turn.role == "user":
            out.append(types.Content(role="user", parts=[types.Part(text=turn.text)]))
        elif turn.role == "model":
            parts = [types.Part.from_function_call(name=c.name, args=c.args if isinstance(c.args, dict) else {})
                     for c in turn.tool_calls]
            out.append(types.Content(role="model", parts=parts or [types.Part(text=turn.text)]))
        else:  # tool results go back as a user-role turn of function responses
            parts = [types.Part.from_function_response(name=n, response={"result": text})
                     for n, text in turn.tool_results]
            out.append(types.Content(role="user", parts=parts))
    return out


class GeminiClient:
    def __init__(self, api_key: SecretStr, model: str, client=None) -> None:
        self._model = model
        self._client = client or genai.Client(api_key=api_key.get_secret_value())  # key is not kept on self

    async def step(self, system: str, history: list[Turn], tools: list[dict]) -> LlmReply:
        declarations = [types.FunctionDeclaration(name=t["name"], description=t["description"],
                                                  parameters_json_schema=sanitize_schema(t["parameters"]))
                        for t in tools]
        config = types.GenerateContentConfig(
            system_instruction=system, tools=[types.Tool(function_declarations=declarations)],
            automatic_function_calling=types.AutomaticFunctionCallingConfig(disable=True))
        response = await self._generate(history, config)
        return LlmReply(tool_calls=[ToolCall(c.name, dict(c.args or {})) for c in response.function_calls or []])

    async def final(self, system: str, history: list[Turn], schema: dict) -> str:
        config = types.GenerateContentConfig(
            system_instruction=system, response_mime_type="application/json",
            response_json_schema=sanitize_schema(schema))
        response = await self._generate(history, config)
        if not response.text or not response.text.strip():
            raise ValueError("empty response")  # blocked or empty: not retryable, nothing is posted
        return response.text

    async def _generate(self, history: list[Turn], config: types.GenerateContentConfig):
        try:
            return await self._client.aio.models.generate_content(
                model=self._model, contents=_contents(history), config=config)
        except errors.APIError as exc:
            if exc.code == 429 or exc.code >= 500:
                raise LlmRetryableError(f"gemini status {exc.code}") from None  # fixed text, no cause chain
            raise
        except Exception as exc:  # CancelledError is a BaseException and is never caught here
            if _is_transport_error(exc):
                raise LlmRetryableError("gemini network error") from None
            raise


def build_llm(settings: Settings) -> GeminiClient | None:
    """None without a key: the copilot still starts and triage is skipped."""
    if settings.gemini_api_key is None:
        return None
    return GeminiClient(settings.gemini_api_key, settings.gemini_model)
