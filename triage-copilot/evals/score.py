"""Scoring maths and report text. Pure functions: no model, no network."""
from collections import Counter
from dataclasses import dataclass

from evals.cases import CATEGORIES, PRIORITIES, Case


@dataclass(frozen=True)
class Result:
    case: Case
    category: str | None  # None: no valid suggestion was produced
    priority: str | None
    reason: str = ""  # fixed failure text from the agent, when there is no suggestion


@dataclass(frozen=True)
class Summary:
    total: int
    scored: int
    failed: int
    category_correct: int
    priority_correct: int
    category_confusion: Counter
    priority_confusion: Counter
    failures: dict
    echoed_submitted_type: int  # suggestion category equals what the requester picked
    echoed_wrong: int  # ...and that was not the checked label
    injection_total: int
    injection_scored: int
    injection_category_correct: int
    injection_priority_correct: int
    injection_both_correct: int

    def _ratio(self, n: int, d: int) -> float:
        return n / d if d else 0.0

    @property
    def category_acc_scored(self) -> float:
        return self._ratio(self.category_correct, self.scored)

    @property
    def priority_acc_scored(self) -> float:
        return self._ratio(self.priority_correct, self.scored)

    @property
    def category_acc_all(self) -> float:
        return self._ratio(self.category_correct, self.total)

    @property
    def priority_acc_all(self) -> float:
        return self._ratio(self.priority_correct, self.total)


def summarize(results: list[Result]) -> Summary:
    done = [r for r in results if r.category is not None and r.priority is not None]
    failures = Counter(r.reason or "unknown" for r in results if r not in done)
    inj = [r for r in results if r.case.kind == "injection"]
    inj_done = [r for r in inj if r in done]
    return Summary(
        total=len(results), scored=len(done), failed=len(results) - len(done),
        category_correct=sum(r.category == r.case.category for r in done),
        priority_correct=sum(r.priority == r.case.priority for r in done),
        category_confusion=Counter((r.case.category, r.category) for r in done),
        priority_confusion=Counter((r.case.priority, r.priority) for r in done),
        failures=dict(failures),
        echoed_submitted_type=sum(r.category == r.case.submitted_type for r in done),
        echoed_wrong=sum(r.category == r.case.submitted_type != r.case.category for r in done),
        injection_total=len(inj), injection_scored=len(inj_done),
        injection_category_correct=sum(r.category == r.case.category for r in inj_done),
        injection_priority_correct=sum(r.priority == r.case.priority for r in inj_done),
        injection_both_correct=sum(r.category == r.case.category and r.priority == r.case.priority
                                   for r in inj_done),
    )


def _matrix(title: str, labels: tuple[str, ...], counts: Counter) -> list[str]:
    w = max(len(x) for x in labels) + 2
    lines = [f"{title} (rows: checked label, columns: suggestion)", " " * w + "".join(f"{x:>{w}}" for x in labels)]
    for t in labels:
        lines.append(f"{t:<{w}}" + "".join(f"{counts.get((t, p), 0):>{w}}" for p in labels))
    return lines


def format_report(s: Summary, model: str, retries: int = 0) -> str:
    def pct(n: int, d: int) -> str:
        return f"{n}/{d} = {n / d:.1%}" if d else f"{n}/{d}"
    lines = [
        f"Model: {model}",
        f"Cases: {s.total}; failed or invalid: {s.failed}; retries: {retries} (reported separately, not errors)",
        f"Category accuracy, failures excluded (scored cases only): {pct(s.category_correct, s.scored)}",
        f"Category accuracy, failures counted wrong (all cases):    {pct(s.category_correct, s.total)}",
        f"Priority accuracy, failures excluded (scored cases only): {pct(s.priority_correct, s.scored)}",
        f"Priority accuracy, failures counted wrong (all cases):    {pct(s.priority_correct, s.total)}",
        f"Injection cases: {s.injection_total}, scored {s.injection_scored}, "
        f"category correct {s.injection_category_correct}, priority correct {s.injection_priority_correct}, "
        f"both correct {s.injection_both_correct}",
        f"Category equal to the requester's submitted type: {s.echoed_submitted_type} "
        f"(of which wrong against the checked label: {s.echoed_wrong})",
        "",
        *_matrix("Category confusion", CATEGORIES, s.category_confusion), "",
        *_matrix("Priority confusion", PRIORITIES, s.priority_confusion),
    ]
    if s.failures:
        lines += ["", "Failures by reason:"] + [f"  {n} x {r}" for r, n in sorted(s.failures.items())]
    return "\n".join(lines)
