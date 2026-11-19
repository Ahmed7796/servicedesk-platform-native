"""Canned demo mode (COPILOT_MODE=canned): a deterministic stand-in for the model. No LLM, no key, no network.

CannedLlm implements the same LlmClient interface, so triage() runs unchanged: the real get_request tool,
TriageSuggestionInput validation and the single typed post. Nothing here can write. Every summary starts with
PREFIX so a reviewer sees on the phone that it is a demo suggestion. Request text is only matched against our own
keywords and is never copied into the output.
"""
import json
import re

from app.errors import LlmRetryableError
from app.llm import LlmReply, ToolCall, Turn
from app.prompt import CLOSE, OPEN, request_id_from_prompt

PREFIX = "[Canned demo] "
CONFIDENCE = 0.7  # above the default low-confidence threshold (0.5), so a canned suggestion is never dropped

_ROUTES = {
    "Equipment": ["DepartmentHead", "ItManager"],
    "SoftwareAccess": ["DepartmentHead", "ItManager"],
    "Leave": ["DepartmentHead", "HrManager"],
    "Purchase": ["DepartmentHead", "FinanceManager"],
}
# Ordered: the first category with a matching keyword wins. Keywords match at the start of a word.
_RULES = [
    ("Equipment", ("laptop", "monitor", "keyboard", "headset", "docking", "hardware")),
    ("SoftwareAccess", ("access", "license", "licence", "account", "software", "vpn", "install")),
    ("Leave", ("leave", "vacation", "holiday", "sick", "day off")),
    ("Purchase", ("purchase", "buy", "invoice", "budget", "order", "vendor")),
]
_HIGH = ("urgent", "asap", "immediately", "outage", "broken")


def _find(text: str, words: tuple[str, ...]) -> str | None:
    for w in words:
        if re.search(rf"\b{re.escape(w)}", text):
            return w
    return None


def classify(title: str, description: str, submitted_type: str) -> dict:
    text = f"{title} {description}".lower()
    category, hit = submitted_type, None
    for cat, words in _RULES:
        hit = _find(text, words)
        if hit:
            category = cat
            break
    if category not in _ROUTES:
        raise ValueError("unknown request type")
    priority = "High" if _find(text, _HIGH) else "Normal"
    why = f"keyword '{hit}' matched" if hit else "no keyword matched, kept the submitted type"
    return {"category": category, "priority": priority, "suggestedRoute": list(_ROUTES[category]),
            "summary": f"{PREFIX}Looks like {category} ({why}); priority {priority}.", "confidence": CONFIDENCE}


class CannedLlm:
    async def step(self, system: str, history: list[Turn], tools: list[dict]) -> LlmReply:
        request_id = _request_id(history)  # ValueError on a malformed prompt: triage reports failed, posts nothing
        if any(t.role == "tool" for t in history):
            return LlmReply()
        return LlmReply(tool_calls=[ToolCall("get_request", {"request_id": request_id})])

    async def final(self, system: str, history: list[Turn], schema: dict) -> str:
        _request_id(history)
        result = _tool_result(history)
        if result.get("error") == "api_unavailable":
            raise LlmRetryableError("servicedesk api unavailable")  # transient: retry, as llm mode effectively does
        if result.get("found") is not True:
            raise ValueError("request not available")
        title, description, rtype = result.get("title"), result.get("description"), result.get("type")
        if not all(isinstance(v, str) for v in (title, description, rtype)):
            raise ValueError("unexpected request shape")
        return json.dumps(classify(title, description, rtype))


def _request_id(history: list[Turn]) -> str:
    first = history[0] if history else None
    if first is None or first.role != "user":
        raise ValueError("unexpected history")
    return request_id_from_prompt(first.text)


def _tool_result(history: list[Turn]) -> dict:
    for turn in history:
        for name, wrapped in turn.tool_results:
            if name == "get_request" and wrapped.startswith(OPEN) and wrapped.endswith(CLOSE):
                data = json.loads(wrapped[len(OPEN):-len(CLOSE)])
                if isinstance(data, dict):
                    return data
    raise ValueError("no get_request result")
