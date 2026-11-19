# Hand-off: M3-3b-fix Gemini live-call fixes

**Agent:** copilot-python  **Date:** 2026-11-18

## What changed (all under `triage-copilot/`)
- `app/llm.py`: new `Opaque` wrapper (repr is fixed `<opaque>`; copy, pickle and JSON paths raise). `ToolCall.state: Opaque | None` (`repr=False`, `compare=False`). `LlmClient` is unchanged.
- `app/gemini.py`: `step()` now reads the response parts (`response.function_calls` drops the signature) and builds one `ToolCall` per function-call part, with that part's `thought_signature` in `state`. `_contents()` sets it back, unchanged, on the same rebuilt part. Text parts are ignored. `GeminiClient(timeout_ms=15000)` sets `types.HttpOptions(timeout=...)`; `build_llm` passes `llm_timeout_seconds * 750` ms.
- `app/config.py`: default `gemini_model` is `gemini-3.5-flash-lite`.
- `app/gemini_smoke.py`: replays the model's own `get_request` call (with its signature) and adds only the fixed sample as the tool result; prints `FAILED: ...` if the model asks for something else. Default model changed. Still posts nothing, no ServiceDesk call, no key printed.
- `app/agent.py`: not changed (it already passes the reply's `ToolCall` objects into history; a test proves the same object arrives).
- Tests: `tests/fake_genai.py` (now returns `candidates` parts, optional signature and a leading text part), `tests/test_gemini.py` (+8), `tests/test_agent.py` (+1), `tests/test_settings.py` (default model assertion). No existing assertion weakened. 162 passed (was 153).

## SDK facts (3.5.0, checked in python:3.12 and Context7)
- `Part.thought_signature` is `Optional[bytes]`. `HttpOptions.timeout` is in **milliseconds**.
- Retries: with `retry_options=None` the SDK uses a never-retry strategy (tenacity is installed but unused). I left it unset and a test asserts it is None. So one attempt per call. The 15 s applies per phase in the httpx transport (connect, read, ...), not to the whole call: a slowly dripping response is bounded by the agent's 20 s `wait_for`, not by the 15 s. The smoke script has no such outer bound, so its worst case is not strictly 30 s. M3-3c's bounded retry is the only retry. The smoke run makes two calls; on a 503 or a dead connection it ends within about 30 s.
- The SDK's aiohttp transport retries connection errors even with `retry_options=None` (`_api_client.py` ~line 2202). aiohttp is not installed; `test_aiohttp_is_not_installed` fails if it ever is.
- Gemini only requires the signature on function-call parts; I echo only those. Signatures on text parts are not sent back (the final call has no text model turns in history).

## How it was verified
- Tests first: 8 failed for the right reasons, then the code.
- `docker run --rm -v "$PWD":/repo -w /repo/triage-copilot python:3.12-slim sh -c "pip install -q -r requirements-dev.txt && pytest -q"` from the worktree root: 162 passed.
- Mutations (each restored): signature not set in `_contents` -> 2 failed; `Opaque.__repr__` leaking the value -> 1 failed.
- `python -m app.gemini_smoke` without a key prints `Set GEMINI_API_KEY first.`
- Live call not run (lead), command below.

## Live command (lead), from the worktree root
`docker run --rm -e GEMINI_API_KEY -e GEMINI_MODEL=gemini-3.5-flash-lite -v "$PWD":/repo -w /repo/triage-copilot python:3.12-slim sh -c "pip install -q -r requirements.txt && python -m app.gemini_smoke"`
Expect `tools requested: ['get_request']` then one validated suggestion JSON line.

## Lead to change (not touched by me)
- `.env.example`: `GEMINI_MODEL=gemini-2.5-flash` -> `GEMINI_MODEL=gemini-3.5-flash-lite` (and its comment).
- `docker-compose.yml`, `triage-copilot` environment: `GEMINI_MODEL: ${GEMINI_MODEL:-gemini-2.5-flash}` -> `${GEMINI_MODEL:-gemini-3.5-flash-lite}`.
- M3-3b hand-off still mentions the old default and the missing `HttpOptions` timeout; this one supersedes it.

## Decisions made
- Opaque wrapper on `ToolCall` rather than a Gemini type in `llm.py`, so the interface stays provider-neutral. Alternative: carry the raw `Content` in `Turn`.
- `Opaque` refuses copy/pickle (`__reduce__` raises) so `dataclasses.asdict`, `copy.deepcopy`, pickle, `json.dumps` and pydantic dumps cannot carry it. Tests cover json, pydantic, pickle and deepcopy of the object itself; the posted body and logs are checked in an end-to-end agent test.
- SDK timeout 15 s (75% of 20 s), derived from the setting, not a new setting.

## Review this hardest
- `gemini.py::_tool_calls`: reads only the first candidate; a candidate with `content=None` (blocked or early stop) gives no calls, so the agent goes on to `final()` (tested); a function-call part with an empty signature gets `state=None`.
- That `reveal()` is called only in `_contents()`.
- `reply.tool_calls[:MAX_CALLS_PER_ROUND]` in the agent can drop later parallel calls; their signatures go with them, which is fine because they are not echoed either.

## Not done / follow-ups
- Live smoke (lead). Not committed.

## Contract or rule questions
- None. Rule 6 unchanged (only the two read tools are declared); position stays an opaque string.
