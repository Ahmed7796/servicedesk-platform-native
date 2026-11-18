# Build log

One entry each time an agent was wrong and something caught it. Keep entries short and factual.

## Progress summary

Updated by the lead at each milestone boundary, or when a long session is handed on, so the next session can start from here rather than from a transcript. The table of agent mistakes follows below.

**As of 2026-11-16, end of Milestone 0** (history; the current state follows). All Milestone 0 tasks are done (M0-5 closes with PR #4).

| Task | PR | Result |
|---|---|---|
| M0-1 | (scaffolding) | `servicedesk-api/` moved into the monorepo; 98 tests pass from the new location |
| M0-2 | #1 | Event envelope plus six event schemas, with examples; `contracts/validate.sh` (ajv, formats enforced) |
| M0-3 | #2 | OpenAPI 3.1 for all three services; `contracts/lint.sh` (Spectral). Existing endpoints checked against the running API. **Gate A passed: contracts frozen** |
| M0-4 | #3 | Compose: SQL Server, PostgreSQL, RabbitMQ, Redis, API. All secrets in `.env`; `/health` returns Healthy |
| M0-5 | #4 | First builder task (api-dotnet). Password removed from `appsettings.Development.json`; a missing connection string fails with a message naming `ConnectionStrings__Default`; 102 tests |

**Highlights**
- **Contract decisions:**
  - `approverUserIds` on the step that must now be acted on, resolved at publish time (an empty list means the step is unassigned and admins are told).
  - Consumers ignore unknown fields.
  - The copilot uses the `TriageService` role through `POST /api/auth/service-token`, and gets 403 on everything except reading requests and posting triage.
  - Triage fields accepted "for now".
- **Plan changes:**
  - New tasks M0-5, M1-0 (`GetEligibleApproversAsync`) and M1-5 (audit trail).
  - M1-4 split into M1-4a and M1-4b.
  - M3-2 dropped its workflow-template tool.
  - PLAN.md gained a Status column.
- **Where checks paid off:**
  - The reviewer found 3 blocking issues across M0-2 to M0-4, all in M0-3. The most important: the copilot's role could reach approve, reject and cancel, and no acceptance check would have noticed.
  - Validating against the running API found a 403 with an empty body that the code-reading missed.
  - Ahmed's "stop if the domain can't resolve it" instruction caught a contract field nothing could fill.
- **Process:** the reviewer stays on `opus` (decided after /usage). Reviewer briefs now name the files to read, to keep cost down.
- **First delegation (M0-5):** the plan-then-approve loop worked. The builder stopped correctly on a missing project reference, and the lead caught line-ending churn before review.

**Decision, 2026-11-17: `servicedesk-api` moves to PostgreSQL (task M0-6).**
- Why: SQL Server needs at least 2 GB RAM to start (too much for M6-4's one small server) and its image is x86-64 only (the M6-1 quick start would fail or emulate on Apple Silicon). The stack already runs PostgreSQL.
- Against, judged weaker than the project's scope: "C# + SQL Server" is the usual enterprise pairing; about half a day of work.
- Rule 2 kept: the API gets its own PostgreSQL container, separate from notification-service's.
- **Exception to "never edit a committed migration", authorized by Ahmed:** `InitialCreate` and `AddOutbox` are SQL Server-specific and are deleted and regenerated for PostgreSQL. Safe only because nothing is deployed and there is no data to keep. From M0-6 on, the rule applies again with no exceptions.
- Verified on compose (lead, 2026-11-17): `InitialPostgres` applies; submit + approve write two outbox rows matching their audit timestamps, both ajv-valid. Racing approve against cancel 15 times gave 14 overlaps: each time exactly one call won, the other got 409 "Concurrency conflict", and the loser's outbox row was rolled back. This closes M1-1b's known limit (rollback on a concurrency conflict untested against a real database).

**As of 2026-11-18, Milestone 1 nearly done.** Everything below is merged except M1-4b (PR #13, open at hand-over). Only M1-5 is left in Milestone 1.

| Task | PR | Result |
|---|---|---|
| M1-0 | #5 | `ApproverResolver.GetEligibleApproversAsync`, tested exhaustively against `CanDecideCurrentStep`; 112 tests |
| M1-1a | #6 | `OutboxMessage` table and `OutboxEventFactory` for the six events, checked against the contract examples; 155 tests |
| M1-1b | #7 | One outbox row per state change, in the same `SaveChanges`; 188 tests |
| M0-6 | #8 | API moved to PostgreSQL (own `api-db` container, `xmin` concurrency, migrations regenerated once as `InitialPostgres`). Racing updates give 409 and roll back the loser's outbox row |
| M1-2a | #9 | `OutboxDispatcher` behind `IEventBroker`; contract gained a Transport (RabbitMQ) section; 207 tests |
| M1-2b | #10 | `OutboxPublisherService` loop + `RabbitMqEventBroker` (RabbitMQ.Client 7.2.2, publisher confirms), `--healthcheck` probe, api health check in Compose; 234 tests |
| M1-3 | #11 | `GET /api/service-requests/pending-for-me`; 251 tests |
| M1-4a | #12 | `POST /api/auth/service-token`, `TriageService` role, deny-by-default authorization (fallback policy = human roles); Mvc.Testing in-memory HTTP tests; 283 tests |
| M1-4b | #13 | `POST /api/service-requests/{id}/triage` (own `TriageSuggestions` table, `ON CONFLICT` upsert), `triage` on detail and pending items, integer enums refused API-wide; 332 tests |

**How the loop runs now** (see ORCHESTRATION.md for the full loop):
- Lead briefs a fresh api-dotnet agent per task, plan only. Lead approves plans when no new dependency, contract change or rule exception is involved; otherwise Ahmed decides.
- Lead verifies every task in compose against real PostgreSQL/RabbitMQ with seeded users, and validates JSON with ajv. OpenAPI component schemas are converted to JSON Schema by a short script; `jsonschema` is not installed.
- Seed data never reaches the Admin fallback (no workflow has a GeneralManager step). Compose checks switch the Leave step to GeneralManager in the throwaway dev DB and wipe it with `docker compose down -v` afterwards.
- Size: builders' test estimates run 1.5 to 2 times low (see the M1-2a to M1-4b row below). Split at plan time.

**Open decisions for Ahmed**
- **Approval tiers** (raised 2026-11-17): the lead recommended holding the refactor until after M6. The suggested model is roles as data with a rank, optional reporting-line steps, and an audited Admin override instead of a final tier. A design note `docs/design/approval-hierarchy.md` was offered but not written.
- **Loosen `position` in the contracts to a plain string?** This is only cheap before M2-1 starts, because consumers will code against the enum. It needs Gate A approval.

**Next**
- **M1-5:** `auditTrail` and `requesterName` on `GET /api/service-requests/{id}`, validating against `ServiceRequestDetail`. M1-4b already returns `ServiceRequestDetailDto` with `triage`, so M1-5 adds the two fields. Remove the now-unused `ServiceRequestService.GetByIdAsync` (flagged in the M1-4b hand-off). Name the stable tie-break for `Submitted` + `StepEscalated` written at the same instant. Seed data for an escalation entry: ops.head submits Equipment.
- **Then Milestones 2 to 4 in parallel** (ORCHESTRATION.md), after a progress-summary update at the milestone boundary.

**Open follow-ups**
- **M2-1 and M3-1 (consumers):** declare exchange `servicedesk.events` (topic, durable) before binding a queue; the API declares it only on its first connection. The lead adds `depends_on: <consumer>: condition: service_healthy` to the api in Compose (contracts/events/README.md, Transport).
- **M2 briefs:** an empty `approverUserIds` means "unassigned, tell admins" (contracts/events/README.md). An Admin's `pending-for-me` is usually empty, which matters for the M4-3 screen.
- **M3 briefs (copilot-python):** use `POST /api/auth/service-token` with `TRIAGE_CLIENT_ID/SECRET` (tokens last 30 min; get a new one on 401). Send real JSON numbers (the API accepts numbers as strings, but don't rely on it) and enum names, never integers.
- **Local dev:** `dotnet run` and `dotnet ef migrations add` need `ConnectionStrings__Default`, `Jwt__Key`, `RabbitMq__Host/User/Password` and `ServiceClients__Triage__ClientId/ClientSecret` (secret at least 32 characters); dummy values are enough for `ef`. Consider a design-time `DbContext` factory. README line in M6-1.
- **Ahmed's machine:** the old SQL Server volume can go (`docker volume rm servicedesk-platform-native_mssql-data`), as can `MSSQL_SA_PASSWORD` in `.env`.
- **Before M6-4:** `ASPNETCORE_ENVIRONMENT=Development` in Compose; no rate limiting on `service-token`; single API instance assumed by the outbox publisher.
- The generated `/openapi/v1.json` types enums as integers (api-dotnet; no task yet).
- `validate.sh`, `lint.sh` and `docker compose up` don't run in CI.
- Consider a `.gitattributes` to pin line endings.

## Agent mistakes

| Date | Task | What the agent did | What caught it | Rule added afterwards |
|---|---|---|---|---|
| (earlier project) | ServiceDesk API | Wired the demo seeder into startup for every environment | Diff review | Seeding runs only in Development (now in CLAUDE.md) |
| 2026-11-16 | M0-2 | Lead proposed `approverUserIds` in the event contract although the API has no way to resolve approvers for a step (`IUserRepository` has only `GetByIdAsync` and `GetByEmailOrEmployeeNumberAsync`) | Ahmed's instruction to stop if the domain can't resolve it, checked before any schema was written | Contract fields the API must fill need a named task that produces them (added M1-0) |
| 2026-11-16 | M0-2 | Lead's first README draft described the Admin fallback as replacing position holders; `EnsureCanDecideCurrentStep` adds Admins and still accepts other holders of the position | Lead re-reading the README against the domain code before review | Resolution rules are stated as steps and M1-0 must test them against `EnsureCanDecideCurrentStep` for every user |
| 2026-11-16 | M0-2 | Lead wrote M1-0's acceptance check over seeded data only; no seeded workflow has a GeneralManager step, so the Admin fallback would never be tested | Reviewer subagent | Acceptance checks for permission rules must name a case that reaches each branch |
| 2026-11-16 | M0-3 | Lead made `TriageSuggestion` extend `TriageSuggestionInput`, which had `additionalProperties: false`, so the response's own fields would have been rejected | Lead re-reading the schema before lint | Request schemas that forbid extra fields are never reused as bases; shared fields go in a separate base schema |
| 2026-11-16 | M0-3 | Lead's contract said every 403 has a ProblemDetails body; the API returns an empty body when `[Authorize(Roles=...)]` refuses | Validating live API responses against the contract | Contracts for existing endpoints are checked against the running API, not only against the code |
| 2026-11-16 | M0-3 | Lead's contract said the copilot's role "cannot" act but gave no 403 on approve/reject/cancel, and M1-4's check never tested a service token against them; today the refusal would only happen by accident (non-GUID `sub` → 401) | Reviewer subagent | Every rule in CLAUDE.md's architecture list that a task touches needs a negative test named in the acceptance check |
| 2026-11-16 | M0-5 | api-dotnet rewrote `ServiceDesk.Api.csproj` from CRLF to LF line endings (and churned `appsettings.Development.json`), so a 1-line change showed as 27 changed lines | Lead comparing `git diff --stat` with `--ignore-cr-at-eol` before review | Builders keep each file's existing line endings; lead checks both stats before sending a diff to review. Consider a `.gitattributes` |
| 2026-11-16 | M1-1 | Lead briefed M1-1 as one task; the builder's plan came to ~770 lines (code + tests), over the 300-line rule, and the builder only planned to stop if production code passed 300 | Lead adding up the line counts in the plan before approving it | Lead totals code + tests in every plan before approval; M1-1 split into M1-1a / M1-1b |
| 2026-11-17 | M0-2 | Lead's event contract defined message bodies but no transport (exchange, routing keys, message properties, delivery guarantee), although three services must agree on it | Lead preparing the M1-2 brief, before any publisher code | Transport section added to `contracts/events/README.md` (approved by Ahmed). A contract between services names the wire details, not only the payload |
| 2026-11-17 | M1-2b | Lead wrote an acceptance clause ("a GeneralManager requester's event lists Admins") that seeded data cannot reach: no seeded workflow has a GeneralManager step, the same gap logged for M0-2 | Lead checking `DbSeeder` and `EscalationPolicy` before the compose run | Lead ran it by switching the Leave step to GeneralManager in the throwaway dev database (wiped afterwards). Before writing an acceptance clause, the lead names the seeded data or setup that reaches it |
| 2026-11-17 | M1-4a | Lead told Ahmed both triage variables were in `.env` after checking only `TRIAGE_CLIENT_SECRET`; `TRIAGE_CLIENT_ID` was missing and the first compose run never started | The compose run itself (interpolation error), and the reviewer's `docker compose config -q` | Lead checks every new variable with `docker compose config -q` before saying the environment is ready |
| 2026-11-18 | M1-2a to M1-4b | Builders' size estimates missed in every task, always on tests: estimates of ~305 to ~405 lines ended at ~400 to ~640, with tests 1.5 to 2 times the estimate | Lead comparing each plan's estimate with the final diff stat | Lead doubles the builder's test estimate when judging the 300-line rule, and splits at plan time when code + 2 × tests is well over 300 |
| 2026-11-18 | M1-5 | api-dotnet's plan used a PostgreSQL identity column as the audit tie-break, on the assumption that rows from one `SaveChanges` are inserted in `Add` order. EF Core sorts inserts within a table by key, and same-millisecond v7 GUIDs sort randomly, so the order would still be unstable. The lead's brief also said "seed data" for what was meant as a compose step, and the builder planned a seed change | Lead checking where audit rows are created and how EF orders insert commands before approving the plan | Domain sets `Sequence` (count + 1) on append; no ordering is left to the database. Briefs say "compose check via the API" when they mean that, not "seed data" |
| 2026-11-18 | M1-5a | The backfill broke same-timestamp ties by v7 `Id`, which is random within a millisecond, so old Submitted/StepEscalated pairs could be numbered backwards. The lead had seen this and called it a "best guess" note, and the compose check passed because one pair comes out right half the time | Reviewer | Backfill ranks `Submitted` first before `Id`. A check that can pass by chance proves nothing: for ordering, the lead tests enough cases or reasons it through instead of trusting one run |
