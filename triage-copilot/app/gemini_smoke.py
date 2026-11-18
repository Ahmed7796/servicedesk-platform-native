"""Manual check against the real Gemini API: GEMINI_API_KEY=... python -m app.gemini_smoke

Sends only the fixed sample below. Posts nothing, never calls the ServiceDesk API, never prints the key
or the model's raw text: it prints the validated suggestion, or the error class name.
"""
import asyncio
import os

from pydantic import SecretStr, ValidationError

from app.gemini import GeminiClient
from app.llm import ToolCall, Turn
from app.models import TriageSuggestionInput
from app.prompt import SYSTEM, user_prompt, wrap_data
from app.tools import TOOL_SPECS

SAMPLE = {"title": "Replacement laptop", "description": "My laptop battery no longer holds a charge.",
          "steps": [{"position": "DepartmentHead"}, {"position": "FinanceManager"}]}


async def main() -> None:
    if not os.environ.get("GEMINI_API_KEY"):
        print("Set GEMINI_API_KEY first.")
        return
    llm = GeminiClient(SecretStr(os.environ["GEMINI_API_KEY"]), os.environ.get("GEMINI_MODEL", "gemini-2.5-flash"))
    rid = "11111111-1111-1111-1111-111111111111"
    history = [Turn("user", user_prompt(rid))]
    try:
        reply = await llm.step(SYSTEM, history, TOOL_SPECS)  # checks the tool declarations are accepted
        print("tools requested:", [c.name for c in reply.tool_calls])
        history += [Turn("model", tool_calls=(ToolCall("get_request", {"request_id": rid}),)),
                    Turn("tool", tool_results=(("get_request", wrap_data(SAMPLE)),))]
        raw = await llm.final(SYSTEM, history, TriageSuggestionInput.model_json_schema(by_alias=True))
        print(TriageSuggestionInput.model_validate_json(raw).model_dump_json(by_alias=True))
    except ValidationError:
        print("FAILED: ValidationError (output did not match the schema)")
    except Exception as exc:
        print(f"FAILED: {type(exc).__name__}")


if __name__ == "__main__":
    asyncio.run(main())
