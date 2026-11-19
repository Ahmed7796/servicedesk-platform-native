import csv
import hashlib
import json
from collections import Counter

import pytest

from evals.cases import CATEGORIES, DRAFT_PATH, PRIORITIES, CasesError, load_checked, load_draft
from evals.finalize import finalize
from evals.make_review import make_review

KINDS = {"plain", "ambiguous", "mislabelled", "injection"}


def test_draft_loads_and_is_balanced():
    cases = load_draft(DRAFT_PATH)
    assert len(cases) == 50
    assert len({c.id for c in cases}) == 50
    cats = Counter(c.proposed_category for c in cases)
    pris = Counter(c.proposed_priority for c in cases)
    assert set(cats) == set(CATEGORIES) and set(pris) == set(PRIORITIES)
    assert all(n >= 10 for n in cats.values()) and all(n >= 10 for n in pris.values())
    kinds = Counter(c.kind for c in cases)
    assert set(kinds) == KINDS and kinds["injection"] >= 5 and kinds["mislabelled"] >= 4
    assert all(c.reason.strip() for c in cases)


def test_mislabelled_cases_submit_a_different_type_than_proposed():
    for c in load_draft(DRAFT_PATH):
        if c.kind == "mislabelled":
            assert c.submitted_type != c.proposed_category, c.id


def test_draft_is_refused_by_the_checked_loader():
    with pytest.raises(CasesError, match="not marked as checked"):
        load_checked(DRAFT_PATH)


def _row(i, **over):
    row = {"id": f"c{i}", "kind": "plain", "title": f"t{i}", "description": f"d{i}",
           "submitted_type": "Equipment", "category": "Equipment", "priority": "Low"}
    row.update(over)
    return row


def _write(path, rows, **meta):
    m = {"checked_by": "Ahmed", "checked_at": "2026-11-20T10:00:00+00:00", "case_count": len(rows),
         "source_sha256": "0" * 64}
    m.update(meta)
    path.write_text("\n".join([json.dumps({"_meta": m})] + [json.dumps(r) for r in rows]) + "\n")


def test_missing_file_is_refused(tmp_path):
    with pytest.raises(CasesError, match="does not exist"):
        load_checked(tmp_path / "cases.jsonl")


def test_valid_checked_file_loads(tmp_path):
    p = tmp_path / "cases.jsonl"
    _write(p, [_row(1), _row(2, category="Leave", priority="Urgent")])
    cases = load_checked(p)
    assert [c.category for c in cases] == ["Equipment", "Leave"]


@pytest.mark.parametrize("meta,rows,message", [
    ({"checked_by": ""}, [_row(1)], "checked_by"),
    ({"checked_by": "   "}, [_row(1)], "checked_by"),
    ({"case_count": 7}, [_row(1)], "case_count"),
    ({"checked_at": ""}, [_row(1)], "checked_at"),
    ({}, [_row(1, category="Banana")], "category"),
    ({}, [_row(1, priority="Critical")], "priority"),
    ({}, [_row(1), _row(1)], "duplicate"),
    ({}, [_row(1, proposed_category="Leave")], "proposed_category"),
    ({}, [], "no cases"),
])
def test_unchecked_or_bad_files_are_refused(tmp_path, meta, rows, message):
    p = tmp_path / "cases.jsonl"
    _write(p, rows, **({"case_count": len(rows)} | meta))
    with pytest.raises(CasesError, match=message):
        load_checked(p)


def test_missing_header_and_bad_json_are_refused(tmp_path):
    p = tmp_path / "cases.jsonl"
    p.write_text(json.dumps(_row(1)) + "\n")
    with pytest.raises(CasesError, match="not marked as checked"):
        load_checked(p)
    p.write_text("{not json\n")
    with pytest.raises(CasesError):
        load_checked(p)


def test_refusal_messages_hold_no_request_text(tmp_path):
    p = tmp_path / "cases.jsonl"
    _write(p, [_row(1, description="SECRET-TEXT", category="SECRET-LABEL")])
    with pytest.raises(CasesError) as exc:
        load_checked(p)
    assert "SECRET" not in str(exc.value)


# review sheet and finalize

def test_make_review_writes_blank_label_columns(tmp_path):
    out = tmp_path / "review.csv"
    make_review(DRAFT_PATH, out)
    rows = list(csv.DictReader(out.open(newline="", encoding="utf-8")))
    assert len(rows) == 50
    assert all(r["category"] == "" and r["priority"] == "" for r in rows)
    assert rows[0]["proposed_category"] in CATEGORIES


def _filled(tmp_path, mutate=None):
    sheet = tmp_path / "review.csv"
    make_review(DRAFT_PATH, sheet)
    rows = list(csv.DictReader(sheet.open(newline="", encoding="utf-8")))
    for r in rows:
        r["category"], r["priority"] = r["proposed_category"], r["proposed_priority"]
    if mutate:
        mutate(rows)
    with sheet.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)
    return sheet


def test_finalize_writes_checked_file_that_loads(tmp_path):
    sheet = _filled(tmp_path)
    out = tmp_path / "cases.jsonl"
    finalize(sheet, out, reviewer="Ahmed")
    cases = load_checked(out)
    assert len(cases) == 50
    meta = json.loads(out.read_text().splitlines()[0])["_meta"]
    assert meta["checked_by"] == "Ahmed" and meta["case_count"] == 50
    assert meta["source_sha256"] == hashlib.sha256(sheet.read_bytes()).hexdigest()
    assert not any("proposed" in line for line in out.read_text().splitlines()[1:])


def test_finalize_accepts_case_insensitive_labels(tmp_path):
    def lower(rows):
        rows[0]["category"] = rows[0]["category"].lower()
        rows[0]["priority"] = rows[0]["priority"].upper()
    out = tmp_path / "cases.jsonl"
    finalize(_filled(tmp_path, lower), out, reviewer="Ahmed")
    assert load_checked(out)[0].category in CATEGORIES


@pytest.mark.parametrize("field,value", [("category", ""), ("priority", " "), ("category", "Banana"),
                                         ("priority", "Critical")])
def test_finalize_refuses_blank_or_bad_labels(tmp_path, field, value):
    out = tmp_path / "cases.jsonl"
    with pytest.raises(CasesError, match=field):
        finalize(_filled(tmp_path, lambda rows: rows[3].update({field: value})), out, reviewer="Ahmed")
    assert not out.exists()


def test_finalize_refuses_blank_reviewer_and_existing_output(tmp_path):
    sheet = _filled(tmp_path)
    out = tmp_path / "cases.jsonl"
    with pytest.raises(CasesError, match="reviewer"):
        finalize(sheet, out, reviewer="  ")
    finalize(sheet, out, reviewer="Ahmed")
    with pytest.raises(CasesError, match="already exists"):
        finalize(sheet, out, reviewer="Ahmed")
