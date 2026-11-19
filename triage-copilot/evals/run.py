"""Run the checked eval cases through the real triage loop: python -m evals.run

Real Gemini model through build_llm; the ServiceDesk API is a stub, so nothing reaches a real API and the
suggestion is captured instead of posted. Only evals/cases.jsonl is read, and only after the checked-file gate.

Assumptions (free tier): about 10 to 15 requests a minute and 2 to 4 model calls per case, so the default of
2 cases at a time, each slot waiting 1 second before it starts a case, keeps a 50-case run slow but inside the limits.
The pause runs inside a concurrency slot, so it applies per slot, not as one global pace.
Retryable outcomes (HTTP 429/5xx, network, timeouts) are retried per case: 3 attempts, waits of 2 s then 6 s.
Retries are reported separately. A case that still fails is counted as failed, never as a wrong answer.
"""
import argparse
import asyncio
import json
import sys
from pathlib import Path

from app.agent import triage
from app.config import Settings
from app.gemini import build_llm
from app.tools import TriageTools
from evals.cases import CHECKED_PATH, Case, CasesError, load_checked
from evals.score import Result, format_report, summarize
from evals.stub_client import StubServiceDesk, request_id_for

RESULTS_DIR = Path(__file__).parent / "results"
BACKOFFS = (2.0, 6.0)  # waits before attempt 2 and attempt 3


def _settings() -> Settings:
    # Reads GEMINI_API_KEY and GEMINI_MODEL from the environment. The ServiceDesk values are unused placeholders.
    return Settings(_env_file=None, servicedesk_api_url="http://stub.invalid", triage_client_id="eval",
                    triage_client_secret="unused")


async def _run_case(case: Case, llm, settings: Settings, sleep, pause: float) -> tuple[Result, int]:
    stub = StubServiceDesk([case])
    rid = request_id_for(case.id)
    await sleep(pause)
    retries = 0
    for attempt in range(len(BACKOFFS) + 1):
        outcome = await triage(rid, llm, TriageTools(stub), stub, settings)  # type: ignore[arg-type]
        if outcome.status == "posted" and rid in stub.captured:
            s = stub.captured[rid]
            return Result(case, s.category, s.priority), retries
        if not outcome.retryable or attempt == len(BACKOFFS):
            return Result(case, None, None, reason=outcome.reason), retries
        retries += 1
        await sleep(BACKOFFS[attempt])
    raise AssertionError("unreachable")


async def run_cases(cases: list[Case], llm, settings: Settings, concurrency: int = 2, pause: float = 1.0,
                    sleep=asyncio.sleep) -> tuple[list[Result], int]:
    gate = asyncio.Semaphore(max(1, concurrency))

    async def one(case: Case):
        async with gate:
            return await _run_case(case, llm, settings, sleep, pause)

    done = await asyncio.gather(*(one(c) for c in cases))
    return [r for r, _ in done], sum(n for _, n in done)


def _write_results(path: Path, model: str, results: list[Result], retries: int) -> None:
    rows = [{"id": r.case.id, "kind": r.case.kind, "label_category": r.case.category,
             "label_priority": r.case.priority, "category": r.category, "priority": r.priority,
             "reason": r.reason} for r in results]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"model": model, "retries": retries, "cases": rows}, indent=1) + "\n",
                    encoding="utf-8")


async def main(argv: list[str] | None = None, *, llm=None, cases_path: Path = CHECKED_PATH,
               sleep=asyncio.sleep) -> int:
    p = argparse.ArgumentParser(prog="python -m evals.run", description=__doc__.split("\n")[0])
    p.add_argument("--limit", type=int, help="run only the first N cases")
    p.add_argument("--concurrency", type=int, default=2)
    p.add_argument("--pause", type=float, default=1.0, help="seconds each concurrency slot waits before a case")
    p.add_argument("--out", type=Path, help="write a results file; must be under evals/results/")
    p.add_argument("--force", action="store_true", help="allow --out to overwrite an existing file")
    args = p.parse_args(argv)

    settings = _settings()
    llm = llm or build_llm(settings)
    if llm is None or settings.gemini_api_key is None:
        print("Set GEMINI_API_KEY first.", file=sys.stderr)
        return 2
    if args.limit is not None and args.limit < 1:
        print("--limit must be 1 or more.", file=sys.stderr)
        return 2
    if args.out is not None:  # all --out checks happen before any model call
        if not args.out.resolve().is_relative_to(RESULTS_DIR.resolve()):
            print("--out must be a path under evals/results/ (git-ignored).", file=sys.stderr)
            return 2
        if args.out.is_dir():
            print("--out must be a file path, not a directory.", file=sys.stderr)
            return 2
        if args.out.exists() and not args.force:
            print("--out already exists; choose another name or pass --force.", file=sys.stderr)
            return 2
    try:
        cases = load_checked(cases_path)
    except CasesError as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        return 2
    if args.limit is not None:
        cases = cases[:args.limit]

    results, retries = await run_cases(cases, llm, settings, args.concurrency, args.pause, sleep)
    print(format_report(summarize(results), model=settings.gemini_model, retries=retries))
    if args.out is not None:
        _write_results(args.out, settings.gemini_model, results, retries)
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
