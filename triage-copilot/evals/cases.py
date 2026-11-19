"""Eval case files and the checked-file gate.

Two kinds of file, never mixed:
- cases.draft.jsonl: written by the agent, has proposed_* labels. The runner never uses it.
- cases.jsonl: written by evals/finalize.py from the human-checked review.csv; first line is a _meta header.
The gate guards against honest mistakes (a missing or unreviewed file). It is not tamper-proof:
anyone can type a name into the header.

Error messages name the problem and the row id, never request text or label values.
"""
import json
from pathlib import Path
from typing import Literal, get_args

from pydantic import BaseModel, ConfigDict, ValidationError

from app.models import RequestType, TriagePriority

CATEGORIES: tuple[str, ...] = get_args(RequestType)
PRIORITIES: tuple[str, ...] = get_args(TriagePriority)
Kind = Literal["plain", "ambiguous", "mislabelled", "injection"]

HERE = Path(__file__).parent
DRAFT_PATH = HERE / "cases.draft.jsonl"
CHECKED_PATH = HERE / "cases.jsonl"


class CasesError(Exception):
    """A case file is missing, unchecked or invalid."""


class _Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


class DraftCase(_Strict):
    id: str
    kind: Kind
    title: str
    description: str
    submitted_type: RequestType  # what the requester picked; it can be wrong on purpose
    proposed_category: RequestType
    proposed_priority: TriagePriority
    reason: str


class Case(_Strict):
    """A checked case. category and priority are Ahmed's labels."""

    id: str
    kind: Kind
    title: str
    description: str
    submitted_type: RequestType
    category: RequestType
    priority: TriagePriority
    note: str = ""


class Meta(_Strict):
    checked_by: str
    checked_at: str
    case_count: int
    source_sha256: str


def _read_lines(path: Path) -> list[dict]:
    try:
        text = Path(path).read_text(encoding="utf-8")
    except FileNotFoundError:
        raise CasesError(f"{Path(path).name} does not exist") from None
    out = []
    for n, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            continue
        try:
            obj = json.loads(line)
        except ValueError:
            raise CasesError(f"{Path(path).name} line {n} is not valid JSON") from None
        if not isinstance(obj, dict):
            raise CasesError(f"{Path(path).name} line {n} is not a JSON object")
        out.append(obj)
    return out


def _fields(exc: ValidationError) -> str:
    return ", ".join(sorted({str(e["loc"][0]) if e["loc"] else "(row)" for e in exc.errors()}))


def _parse(model, objs: list[dict], what: str) -> list:
    items, seen = [], set()
    for n, obj in enumerate(objs, 1):
        try:
            item = model.model_validate(obj)
        except ValidationError as exc:
            raise CasesError(f"{what} row {n} is invalid in field(s): {_fields(exc)}") from None
        if item.id in seen:
            raise CasesError(f"{what} has a duplicate id at row {n}")
        seen.add(item.id)
        items.append(item)
    return items


def load_draft(path: Path = DRAFT_PATH) -> list[DraftCase]:
    return _parse(DraftCase, _read_lines(path), "draft")


def load_checked(path: Path = CHECKED_PATH) -> list[Case]:
    """Refuses unless the file starts with a _meta header naming the reviewer and the count matches."""
    objs = _read_lines(path)
    name = Path(path).name
    if not objs or set(objs[0]) != {"_meta"}:
        raise CasesError(f"{name} is not marked as checked: the first line must be the _meta header "
                         "written by evals.finalize")
    try:
        meta = Meta.model_validate(objs[0]["_meta"])
    except ValidationError as exc:
        raise CasesError(f"{name} has an invalid _meta header ({_fields(exc)})") from None
    if not meta.checked_by.strip():
        raise CasesError(f"{name} is not marked as checked: checked_by is empty")
    if not meta.checked_at.strip():
        raise CasesError(f"{name} is not marked as checked: checked_at is empty")
    rows = objs[1:]
    if not rows:
        raise CasesError(f"{name} has no cases")
    if meta.case_count != len(rows):
        raise CasesError(f"{name}: _meta case_count does not match the number of cases")
    return _parse(Case, rows, "cases")
