"""The one interface the agent needs from a language model. Providers implement it (Gemini: M3-3b)."""
from dataclasses import dataclass, field
from typing import Literal, Protocol


@dataclass(frozen=True)
class ToolCall:
    name: str
    args: object


@dataclass(frozen=True)
class Turn:
    """One step of the conversation. tool_results holds (tool name, wrapped data text)."""

    role: Literal["user", "model", "tool"]
    text: str = ""
    tool_calls: tuple[ToolCall, ...] = ()
    tool_results: tuple[tuple[str, str], ...] = ()


@dataclass(frozen=True)
class LlmReply:
    """Either tool calls to run, or no calls (the model is ready to answer)."""

    tool_calls: list[ToolCall] = field(default_factory=list)


class LlmClient(Protocol):
    async def step(self, system: str, history: list[Turn], tools: list[dict]) -> LlmReply:
        """One model turn with the read tools available."""

    async def final(self, system: str, history: list[Turn], schema: dict) -> str:
        """The answer as JSON text matching `schema`. No tools are offered, so it cannot be a tool call."""
