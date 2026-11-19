import json
import re
from pathlib import Path

import pytest

import evals.run as run_module
from app.errors import LlmRetryableError
from app.llm import LlmReply, ToolCall
from evals.run import main

pytestmark = pytest.mark.anyio

UUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


class ScriptedLlm:
    """Asks for get_request once per case, then answers with `answer(request_text)`. Never touches the network."""

    def __init__(self, answer, fail_first=0, always_fail=False):
        self.answer, self.fail_first, self.always_fail = answer, fail_first, always_fail
        self.step_calls = 0
        self.seen_text = []

    async def step(self, system, history, tools):
        self.step_calls += 1
        if self.always_fail or self.step_calls <= self.fail_first:
            raise LlmRetryableError("gemini status 503")
        if any(t.role == "tool" for t in history):
            return LlmReply()
        rid = UUID_RE.search(history[0].text).group(0)
        return LlmReply([ToolCall("get_request", {"request_id": rid})])

    async def final(self, system, history, schema):
        data = next(t.tool_results[0][1] for t in history if t.role == "tool")
        self.seen_text.append(data)
        return self.answer(data)


def reply(category, priority):
    return json.dumps({"category": category, "priority": priority, "suggestedRoute": ["DepartmentHead"],
                       "summary": "s", "confidence": 0.9})


def good_by_title(data):
    # the model "reads" the title and answers with the title's own label
    cat, pri = re.search(r'"title": "(\w+)-(\w+)"', data).groups()
    return reply(cat, pri)


def _row(i, cat, pri, kind="plain"):
    return {"id": f"c{i}", "kind": kind, "title": f"{cat}-{pri}", "description": f"desc {i}",
            "submitted_type": "Equipment", "category": cat, "priority": pri}


def _cases(tmp_path, rows):
    p = tmp_path / "cases.jsonl"
    meta = {"checked_by": "Ahmed", "checked_at": "2026-11-20T10:00:00+00:00", "case_count": len(rows),
            "source_sha256": "0" * 64}
    p.write_text("\n".join([json.dumps({"_meta": meta})] + [json.dumps(r) for r in rows]) + "\n")
    return p


ROWS = [_row(1, "Equipment", "High"), _row(2, "Leave", "Low"), _row(3, "Purchase", "Urgent", "injection")]


@pytest.fixture(autouse=True)
def env(monkeypatch):
    monkeypatch.setenv("GEMINI_API_KEY", "fake-key-for-tests")
    monkeypatch.setenv("GEMINI_MODEL", "model-under-test")
    for k in ("SERVICEDESK_API_URL", "TRIAGE_CLIENT_ID", "TRIAGE_CLIENT_SECRET"):
        monkeypatch.delenv(k, raising=False)


async def go(tmp_path, llm, rows=ROWS, extra=(), **kw):
    sleeps = []

    async def sleep(s):
        sleeps.append(s)
    code = await main(["--concurrency", "2", *extra], llm=llm, cases_path=_cases(tmp_path, rows), sleep=sleep, **kw)
    return code, sleeps


async def test_fake_llm_end_to_end_scores_and_reports(tmp_path, capsys):
    code, _ = await go(tmp_path, ScriptedLlm(good_by_title))
    out = capsys.readouterr().out
    assert code == 0
    assert "Model: model-under-test" in out
    assert "failures excluded" in out and "failures counted wrong" in out
    assert "3/3 = 100.0%" in out and "failed or invalid: 0" in out and "retries: 0" in out


async def test_wrong_answers_lower_accuracy(tmp_path, capsys):
    llm = ScriptedLlm(lambda data: reply("Equipment", "Low"))
    await go(tmp_path, llm)
    out = capsys.readouterr().out
    assert "Category accuracy, failures excluded (scored cases only): 1/3" in out
    assert "Priority accuracy, failures excluded (scored cases only): 1/3" in out


async def test_invalid_json_counts_as_failed_not_wrong(tmp_path, capsys):
    llm = ScriptedLlm(lambda data: "not json" if "Leave" in data else good_by_title(data))
    await go(tmp_path, llm)
    out = capsys.readouterr().out
    assert "failed or invalid: 1" in out
    assert "Category accuracy, failures excluded (scored cases only): 2/2" in out
    assert "Category accuracy, failures counted wrong (all cases):    2/3" in out
    assert "invalid model output" in out


async def test_retryable_then_success_is_one_retry(tmp_path, capsys):
    llm = ScriptedLlm(good_by_title, fail_first=1)
    code, sleeps = await go(tmp_path, llm, rows=ROWS[:1], extra=["--pause", "0"])
    out = capsys.readouterr().out
    assert code == 0 and "retries: 1" in out and "failed or invalid: 0" in out
    assert "1/1 = 100.0%" in out
    assert [s for s in sleeps if s] == [2]


async def test_persistent_retryable_error_ends_failed_after_three_attempts(tmp_path, capsys):
    llm = ScriptedLlm(good_by_title, always_fail=True)
    code, sleeps = await go(tmp_path, llm, rows=ROWS[:1], extra=["--pause", "0"])
    out = capsys.readouterr().out
    assert llm.step_calls == 3
    assert [s for s in sleeps if s] == [2, 6]
    assert "failed or invalid: 1" in out and "retries: 2" in out and "llm unavailable" in out
    assert "0/1" in out and "Category accuracy, failures excluded (scored cases only): 0/0" in out
    assert code == 0


async def test_non_retryable_failure_is_not_retried(tmp_path, capsys):
    llm = ScriptedLlm(lambda data: "{}")
    await go(tmp_path, llm, rows=ROWS[:1], extra=["--pause", "0"])
    out = capsys.readouterr().out
    assert "retries: 0" in out and "failed or invalid: 1" in out


async def test_labels_never_reach_the_model(tmp_path):
    rows = [{**_row(1, "Leave", "Urgent", "mislabelled"), "title": "Printer jam", "note": "SECRET-NOTE"}]
    llm = ScriptedLlm(lambda data: reply("Leave", "Urgent"))
    await go(tmp_path, llm, rows=rows)
    seen = " ".join(llm.seen_text)
    assert "Printer jam" in seen  # the request text did arrive
    for absent in ("SECRET-NOTE", "Leave", "Urgent", "mislabelled", '"kind"', '"category"', '"priority"'):
        assert absent not in seen, absent


class TrackingLlm(ScriptedLlm):
    """Counts cases in flight: a case starts at its first step and ends at its final."""

    def __init__(self, answer):
        super().__init__(answer)
        self.in_flight = self.max_in_flight = 0

    async def step(self, system, history, tools):
        import asyncio
        if not any(t.role == "tool" for t in history):
            self.in_flight += 1
            self.max_in_flight = max(self.max_in_flight, self.in_flight)
        await asyncio.sleep(0.01)  # lets other cases run while this one waits
        return await super().step(system, history, tools)

    async def final(self, system, history, schema):
        self.in_flight -= 1
        return await super().final(system, history, schema)


@pytest.mark.parametrize("n", [1, 2, 3])
async def test_at_most_n_cases_are_in_flight(tmp_path, n):
    rows = [_row(i, "Leave", "Low") for i in range(1, 8)]
    llm = TrackingLlm(good_by_title)
    code = await main(["--concurrency", str(n), "--pause", "0"], llm=llm, cases_path=_cases(tmp_path, rows),
                      sleep=_nosleep)
    assert code == 0 and llm.max_in_flight == n


async def _nosleep(s):
    import asyncio
    await asyncio.sleep(0)


async def test_pause_value_is_used_once_per_case(tmp_path):
    sleeps = []

    async def sleep(s):
        sleeps.append(s)
    await main(["--pause", "0.5"], llm=ScriptedLlm(good_by_title), cases_path=_cases(tmp_path, ROWS), sleep=sleep)
    assert sleeps.count(0.5) == 3


@pytest.mark.parametrize("limit", ["0", "-1"])
async def test_limit_below_one_exits_2(tmp_path, capsys, limit):
    llm = ScriptedLlm(good_by_title)
    code, _ = await go(tmp_path, llm, extra=["--limit", limit])
    assert code == 2 and "--limit must be 1 or more" in capsys.readouterr().err and llm.step_calls == 0


async def test_out_refuses_directory_and_existing_file_before_the_run(tmp_path, capsys, monkeypatch):
    results = tmp_path / "results"
    results.mkdir()
    monkeypatch.setattr(run_module, "RESULTS_DIR", results)
    llm = ScriptedLlm(good_by_title)
    code, _ = await go(tmp_path, llm, extra=["--out", str(results)])
    assert code == 2 and "directory" in capsys.readouterr().err
    target = results / "r.json"
    target.write_text("old")
    code, _ = await go(tmp_path, llm, extra=["--out", str(target)])
    assert code == 2 and "--force" in capsys.readouterr().err
    assert target.read_text() == "old" and llm.step_calls == 0
    code, _ = await go(tmp_path, llm, extra=["--out", str(target), "--force"])
    assert code == 0 and json.loads(target.read_text())["model"] == "model-under-test"


async def test_no_key_exits_2_before_reading_cases(monkeypatch, tmp_path, capsys):
    monkeypatch.setenv("GEMINI_API_KEY", "")
    code = await main([], cases_path=tmp_path / "does-not-exist.jsonl")
    captured = capsys.readouterr()
    assert code == 2 and "Set GEMINI_API_KEY first." in captured.err + captured.out
    assert "does not exist" not in captured.err + captured.out


async def test_missing_or_unchecked_file_exits_2_with_the_gate_message(tmp_path, capsys):
    code = await main([], llm=ScriptedLlm(good_by_title), cases_path=tmp_path / "cases.jsonl")
    assert code == 2 and "does not exist" in capsys.readouterr().err
    p = tmp_path / "cases.jsonl"
    p.write_text(json.dumps(ROWS[0]) + "\n")
    code = await main([], llm=ScriptedLlm(good_by_title), cases_path=p)
    assert code == 2 and "not marked as checked" in capsys.readouterr().err


async def test_limit_runs_only_the_first_cases(tmp_path, capsys):
    llm = ScriptedLlm(good_by_title)
    await go(tmp_path, llm, extra=["--limit", "2", "--pause", "0"])
    assert len(llm.seen_text) == 2 and "Cases: 2;" in capsys.readouterr().out


async def test_out_must_be_under_results_dir(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(run_module, "RESULTS_DIR", tmp_path / "results")
    code, _ = await go(tmp_path, ScriptedLlm(good_by_title), extra=["--out", str(tmp_path / "elsewhere.json")])
    assert code == 2 and "evals/results" in capsys.readouterr().err
    assert not (tmp_path / "elsewhere.json").exists()


async def test_out_file_holds_labels_and_suggestions_only(tmp_path, monkeypatch):
    monkeypatch.setattr(run_module, "RESULTS_DIR", tmp_path / "results")
    target = tmp_path / "results" / "r.json"
    code, _ = await go(tmp_path, ScriptedLlm(good_by_title), extra=["--out", str(target)])
    data = json.loads(target.read_text())
    assert code == 0 and data["model"] == "model-under-test" and len(data["cases"]) == 3
    assert set(data["cases"][0]) == {"id", "kind", "label_category", "label_priority", "category", "priority",
                                     "reason"}
    assert "desc 1" not in target.read_text()


def test_runner_never_references_the_draft_file():
    src = Path(run_module.__file__).read_text().lower()
    assert "draft" not in src and "review.csv" not in src
