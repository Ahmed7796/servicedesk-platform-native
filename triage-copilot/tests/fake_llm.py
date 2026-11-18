import asyncio

from app.llm import LlmReply


class FakeLlm:
    """Scripted LlmClient. Records what it was shown. Never touches the network."""

    def __init__(self, steps=None, final="{}", delay=0.0, step_error=None):
        self.steps = list(steps or [LlmReply()])
        self.final_text = final
        self.delay = delay
        self.step_error = step_error
        self.step_calls = 0
        self.final_calls = 0
        self.tool_names: list[list[str]] = []
        self.final_history: list = []
        self.final_schema: dict | None = None

    async def step(self, system, history, tools):
        self.step_calls += 1
        self.tool_names.append([t["name"] for t in tools])
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.step_error:
            raise self.step_error
        return self.steps.pop(0) if len(self.steps) > 1 else self.steps[0]  # the last reply repeats

    async def final(self, system, history, schema):
        self.final_calls += 1
        self.final_history = [system, *history]
        self.final_schema = schema
        return self.final_text
