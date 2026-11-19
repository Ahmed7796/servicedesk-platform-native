"""Build the review sheet from the agent's draft: python -m evals.make_review

Writes evals/review.csv with empty category and priority columns for Ahmed to fill in. Never writes cases.jsonl.
"""
import csv
import sys
from pathlib import Path

from evals.cases import DRAFT_PATH, HERE, load_draft

REVIEW_PATH = HERE / "review.csv"
COLUMNS = ["id", "kind", "title", "description", "submitted_type", "proposed_category", "proposed_priority",
           "reason", "category", "priority", "note"]


def make_review(draft: Path = DRAFT_PATH, out: Path = REVIEW_PATH) -> int:
    cases = load_draft(draft)
    with Path(out).open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=COLUMNS, lineterminator="\n")  # LF, like the rest of the repo
        w.writeheader()
        for c in cases:
            w.writerow({**c.model_dump(), "category": "", "priority": "", "note": ""})
    return len(cases)


if __name__ == "__main__":
    print(f"wrote {make_review()} rows to {REVIEW_PATH}", file=sys.stderr)
