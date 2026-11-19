"""The one interface the agent needs from a language model. Providers implement it (Gemini: M3-3b)."""
from dataclasses import dataclass, field
from typing import Literal, Protocol


class Opaque:
    """A provider-specific value (Gemini: a thought signature) that is only handed back to the provider.

    Nothing may read, log or serialise it: repr/str are fixed text, and copy, pickle and JSON paths refuse it."""

    __slots__ = ("_value",)

    def __init__(self, value: object) -> None:
        self._value = value

    def reveal(self) -> object:
        """For the provider adapter only."""
        return self._value

    def __repr__(self) -> str:
        return "<opaque>"

    def __reduce__(self):
        raise TypeError("Opaque cannot be copied or serialised")


@dataclass(frozen=True)
class ToolCall:
    name: str
    args: object
    state: Opaque | None = field(default=None, repr=False, compare=False)  # echoed back to the model, never read


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
