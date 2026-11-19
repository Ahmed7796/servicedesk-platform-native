# Triage evals (demo)

50 labelled requests that measure how often the copilot's suggested category and priority match a human's labels.
Nothing here is production tooling; it is a demo evaluation.

## Files

| File | Who writes it | What it is |
|---|---|---|
| `cases.draft.jsonl` | the agent | 50 requests with `proposed_category`, `proposed_priority` and a `reason`. Never used for scoring. |
| `review.csv` | generated from the draft, edited by the reviewer | The review sheet. Empty `category`, `priority` and `note` columns for the reviewer. |
| `cases.jsonl` | `python -m evals.finalize`, run by the reviewer | The checked cases. First line is a `_meta` header (`checked_by`, `checked_at`, `case_count`, `source_sha256`). Only this file is scored. |
| `results/` | the runner (`--out`) | Optional result files. Git-ignored. |

`submitted_type` is the type the requester picked and the model can see it. It is deliberately wrong in the
"mislabelled" cases. The score compares the suggestion with the reviewer's `category`, never with `submitted_type`.

## Review steps (reviewer)

1. Open `evals/review.csv` in a spreadsheet (UTF-8). Do not rename the columns or change `id`.
2. For every row, fill `category` (Equipment, SoftwareAccess, Leave, Purchase) and `priority` (Low, Normal, High, Urgent),
   even when you agree with the `proposed_*` columns. A blank cell stops the next step. Use `note` for doubts.
   You may fix a title or description that is unrealistic; leave `submitted_type` alone unless it is wrong for its `kind`.
3. Save as CSV (UTF-8), same name and place.
4. From `triage-copilot/`: `python -m evals.finalize --reviewer "Your Name"`. It refuses on any blank or unknown label and
   will not overwrite an existing `cases.jsonl`.
5. Read `cases.jsonl` once. Then it is ready for `python -m evals.run`.

## What the gate does and does not do

`load_checked` (which the runner calls before scoring anything) refuses to load the cases when `cases.jsonl` is missing, has no `_meta` header or an empty `checked_by`, has a
wrong `case_count`, or has an invalid label. This guards against honest mistakes, such as scoring an unreviewed draft.
It is **not tamper-proof**: anyone can type a name into the header or edit the file. The point of the gate is to make the
mistake hard to make, not to prove who labelled the cases. `source_sha256` links `cases.jsonl` to the exact CSV it came from.

## Running

`GEMINI_API_KEY=... python -m evals.run` (optional `GEMINI_MODEL`). It runs every checked case through the real
triage loop and Gemini adapter. The ServiceDesk API is an in-process stub, so nothing reaches a real API and the
suggestion is captured, not posted. Options: `--limit N`, `--concurrency 2` (default), `--pause 1` (seconds each
concurrency slot waits before it starts a case, so the pause is per slot, not a global pace), `--out evals/results/<name>.json`
(labels and suggestions only; git-ignored; refuses a directory or an existing file unless `--force`), `--limit N` (1 or more).

It exits 2 with a message when `GEMINI_API_KEY` is empty (checked first), or when `cases.jsonl` is missing or
unchecked. Retryable errors (429, 5xx, network, timeouts) are retried per case: 3 attempts, waits of 2 s then 6 s.
Retries are printed separately; a case that still fails is counted as failed, not wrong. The report shows two accuracy
figures, "failures excluded" and "failures counted wrong", a confusion matrix for each field, the injection subset and
how often the suggestion just equals the requester's `submitted_type`.

Free-tier rate limits are an assumption of the design: about 10 to 15 requests a minute and 2 to 4 model calls per case,
so a full run takes several minutes at low concurrency. Lower `--concurrency` or raise `--pause` if you see 429s.

## Recorded result

The README rule in PLAN.md (M3-4) is met here until the top-level README exists (M6-1).

| Date | Model | Category accuracy (failures excluded / counted wrong) | Priority accuracy (failures excluded / counted wrong) | Failed or invalid | Retries |
|---|---|---|---|---|---|
|  |  |  |  |  |  |
