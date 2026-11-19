# Hand-off: M2-4b-i overdue claim and push (not scheduled yet)

**Agent:** notify-spring  **Date:** 2026-11-18

**Nothing schedules the job yet.** `OverdueJob.runOnce()` exists and is tested, but no `@Scheduled`, no `@EnableScheduling`, no interval property. In a running service no overdue push happens until M2-4b-ii. The M2-4 acceptance ("exactly one overdue push") is proven here by calling `runOnce()` in tests, not by a running timer.

## What changed
- `db/migration/V4__overdue_since.sql`: `overdue_since timestamptz NULL` on `pending_step`. V1 to V3 untouched.
- `events/PendingStepRepository`: `overdue_since = NULL` in `moveToStep` and in the upsert's `DO UPDATE SET`. Both writes are already behind the strict `last_event_at <` guard, so a stale or repeated event does not reset the claim.
- `overdue/OverdueClaimRepository.claimDue(now, sla)`: one `UPDATE ... SET overdue_since = :now WHERE closed = false AND overdue_since IS NULL AND pending_since < :cutoff RETURNING request_id, title, assigned_user_ids`. `cutoff = now - sla` is computed in Java instead of `pending_since + sla < now` in SQL: same rule as M2-3 (strictly after `dueAt`), and it can use the existing partial index. Returns `PendingNotice` with `type = "step.overdue"`.
- `overdue/OverdueJob.runOnce()`: reads the injected `Clock` once, claims (own statement, commits), then calls `PushNotifier.notifyOverdue` for each. Logs one line with a count.
- `push/PushNotifier`: new `notifyOverdue` (title "Overdue", body "Overdue: <title>"). `notifyPending` and `notifyOverdue` share one private `deliver` method; same recipients (assigned approvers, or Admin devices when empty), same logs. Position is not pushed (not needed by the message), so there is nothing to interpret.
- Tests: new `OverdueJobTest` (16 test runs, `twoConcurrentRuns...` repeated 5 times). `ProjectionHandlerTest.migrationCreatesExpectedColumns` now lists `overdue_since` as the tenth column: the schema grew, the assertion is otherwise the same (not weakened).
- No dependency, no `pom.xml`, no contract, Compose or `.env.example` change. About 100 lines of main code and 300 of tests.

## How it was verified
- `./mvnw test` (Docker up): 151 tests, 0 failures, BUILD SUCCESS (135 before; 150 before the review fixes).
- **Process slip:** I wrote the main classes before the tests. I then checked the tests do bite with two mutations: dropping `overdue_since IS NULL` from the claim failed 10 of 15 (sequential, concurrent, lock-wait, reset and failure tests); removing the reset from the upsert failed `resubmitResetsTheClaim...`; removing it from `moveToStep` failed `stepChangeResetsTheClaim...`. All restored; all pass.
- Acceptance: `dueStepIsClaimedAndPushedOnceAcrossTwoSequentialRuns` and `twoConcurrentRunsPushEachStepExactlyOnce`. Not run in Compose.

## What each test proves
- Sequential: two runs, one push, `overdue_since` is the first run's time.
- Concurrent, job level: two threads released by a `CyclicBarrier`, 5 due steps, 5 distinct pushes, repeated 5 times. This alone could pass by luck.
- Concurrent, claim level (the hard one): run A holds the row locks in an open transaction; the test polls `pg_stat_activity` until run B's UPDATE shows `wait_event_type = 'Lock'`, asserts B is not done, then lets A commit. A claims 3, B claims 0. Both are inside the claim at the same time by construction. Real PostgreSQL 17 via Testcontainers.
- Reset: `step_approved` clears the claim, the new step is not due, then gets its own push when the clock passes its due time; a second run adds nothing. A resubmit with the same instant or an older one leaves `overdue_since` alone; a strictly newer one clears it.
- Skips: closed, not yet due, and exactly due (not overdue); one second past is claimed.
- Sender returning FAILED, and throwing: step stays marked, next run pushes nothing. INVALID_TOKEN deletes the device.
- Unassigned goes to Admin devices only; assigned goes to its approvers only. Wording and `data.type = step.overdue`.
- Logs (captured output) contain no token and no title.

## Decisions made
- Claim then push, at most once, as agreed. A crash between the two loses that push; the step stays marked.
- `data.type` is `step.overdue`. I checked `contracts/`: `grep` for push, overdue and `step.overdue` finds only the device registration schema, the `GET /notify/overdue` path and README lines. Nothing constrains the push payload or its `data.type`, so no contract change is needed.
- The claim is not wrapped in a transaction with the push. Alternative: one transaction for both, rejected (network call while holding row locks).

## Review this hardest
- `OverdueClaimRepository.claimDue` SQL: the `overdue_since IS NULL` guard is the whole once-only guarantee, and `pending_since < cutoff` must match `OverdueRepository` (strict).
- That `OverdueJob.runOnce` is not `@Transactional` (it must not be: the claim has to commit before the first push).
- The `PushNotifier` refactor: `notifyPending` behaviour and log line should be unchanged (existing `PushNotifierTest` still passes).

## Not done / follow-ups
- M2-4b-ii: `OverdueJobProperties` (`NOTIFY_OVERDUE_JOB_INTERVAL`, strict ISO-8601, default `PT1M`), `@EnableScheduling` behind `app.notify.overdue-job.enabled`, `enabled: false` in the `test`, `dbtest` and `pipeline` profiles, a test that the scheduler is absent there, and `ExpoPushSender` connect timeout 10 s with read 5 s.
- The job is still active in no profile today. Because `@EnableScheduling` does not exist yet, adding it in 4b-ii is when the profile flags matter.

## Env vars for the Compose service
None in this half. 4b-ii adds `NOTIFY_OVERDUE_JOB_INTERVAL` (optional, ISO-8601, default `PT1M`).

## Contract or rule questions
- None. See the `data.type` decision above.

## Review fixes (reviewer, no blocking findings)
- `OverdueJob.runOnce` now has a comment saying it must stay non-transactional, plus `runOnceAndItsClassAreNotTransactional` (reflection on Spring's `@Transactional`, method and class; no ArchUnit).
- `PushNotifier`: the `approverUserIds().isEmpty()` check is back inside the `try`, after the `enabled` check, computed once in `deliver`; `notifyPending` and `notifyOverdue` pass both bodies. Behaviour unchanged; `PushNotifierTest` untouched and passing.
