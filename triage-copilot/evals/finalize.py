"""Turn the human-checked review sheet into cases.jsonl: python -m evals.finalize --reviewer "<name>"

Run by the reviewer, not by the agent. Refuses a blank or unknown label on any row, and refuses to overwrite.
"""
import argparse
import csv
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

from evals.cases import CATEGORIES, CHECKED_PATH, PRIORITIES, Case, CasesError
from evals.make_review import REVIEW_PATH


def _label(value: str, allowed: tuple[str, ...], field: str, row_id: str) -> str:
    found = {a.lower(): a for a in allowed}.get(value.strip().lower())
    if found is None:
        raise CasesError(f"row {row_id}: {field} is blank or not one of {', '.join(allowed)}")
    return found


def finalize(sheet: Path = REVIEW_PATH, out: Path = CHECKED_PATH, reviewer: str = "") -> int:
    if not reviewer.strip():
        raise CasesError("reviewer name is required (--reviewer)")
    out = Path(out)
    if out.exists():
        raise CasesError(f"{out.name} already exists; move it away first")
    raw = Path(sheet).read_bytes()
    rows = list(csv.DictReader(raw.decode("utf-8").splitlines(), strict=True))
    if not rows:
        raise CasesError("the review sheet has no rows")
    cases, seen = [], set()
    for r in rows:
        rid = r.get("id", "")
        if rid in seen:
            raise CasesError(f"duplicate id {rid}")
        seen.add(rid)
        try:
            cases.append(Case(
                id=rid, kind=r["kind"], title=r["title"], description=r["description"],
                submitted_type=r["submitted_type"],
                category=_label(r.get("category", ""), CATEGORIES, "category", rid),
                priority=_label(r.get("priority", ""), PRIORITIES, "priority", rid),
                note=r.get("note", "")))
        except (KeyError, ValueError):
            raise CasesError(f"row {rid}: a column is missing or invalid") from None
    meta = {"checked_by": reviewer.strip(), "checked_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "case_count": len(cases), "source_sha256": hashlib.sha256(raw).hexdigest()}
    lines = [json.dumps({"_meta": meta})] + [c.model_dump_json() for c in cases]
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return len(cases)


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--reviewer", required=True)
    try:
        n = finalize(reviewer=p.parse_args().reviewer)
    except CasesError as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        sys.exit(2)
    print(f"wrote {n} checked cases to {CHECKED_PATH.name}")
