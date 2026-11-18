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

**As of 2026-11-18, end of Milestone 1.** Everything below is merged. Milestone 1 is closed; the contract change in PR #16 (`position` as a plain string) is the last thing before Milestones 2 to 4.

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
| M1-5a | #14 | `AuditLog.Sequence` numbered by the domain per request (not by insert order: EF sorts inserts by key and v7 Ids are random within a millisecond); unique (ServiceRequestId, Sequence); migration backfills old rows with Submitted ranked first; 340 tests |
| M1-5b | #15 | `requesterName` and `auditTrail` (ordered by `Sequence`) on `GET {id}`; validates against `ServiceRequestDetail` with ajv on compose; unused `GetByIdAsync` removed; 346 tests |

**As of 2026-11-18, Milestones 2 to 4 under way in parallel.** First round merged:

| Task | PR | Result |
|---|---|---|
| contracts | #16 | `approverPosition` is a plain string (pattern, `maxLength` 30, five `examples`); consumer rule 4; approval-tiers design note |
| M3-1 | #18 | FastAPI skeleton, `/health`, service-token client (cache, refresh 60 s early, one retry on 401, secret as `SecretStr` with `hide_input_in_errors`); `copilot` in Compose; 17 tests |
| M2-1 | #19 | Spring Boot 4.1.2 skeleton: HS256-pinned JWT validation (key ≥ 32 bytes, `exp` required), `/notify/health` per contract, `/actuator/**` denied, Flyway baseline; `notify` in Compose; 28 tests |
| M4-1a | #17 | Expo SDK 58 skeleton, three-screen navigation opening on Login, exact pins |
| M4-1b | #20 | Generated API clients (`@hey-api/openapi-ts`, no default `baseUrl`), required proxy URL with a `ConfigError` view, secure-store token store; `npm test` runs tsc first; 12 tests |

**As of 2026-11-18 (evening), second and third rounds merged.** No PR is open. Main is at `1bd92f8` (PR #31).

| Task | PR | Result |
|---|---|---|
| M3-2a | #22 | `get_request` tool: typed pydantic models (`Literal` for closed enums, `str` for position), compact LLM-facing result, errors as tool results; 31 tests |
| M3-2b | #25 | `get_requester_history` (client-side filter, newest first, cap 1..10), `TOOL_SPECS`, dispatcher; contract-reading test fails instead of skipping; CLAUDE.md copilot test command mounts the whole repo; 74 tests |
| M4-2a | #23 | Login via generated `login()`, plain-language errors, in-flight ref against double submit, token cleared if navigation fails; `npm run smoke:login` developer check; 26 tests |
| M4-2b | #26 | Start on PendingApprovals with a stored token; 401 interceptor ends the session once, and only if the failed request used the stored token; 36 tests |
| M2-2a | #24 | `pending_step` projection (V2 migration) from the six events, guarded by strict `last_event_at < occurredAt`, closed as a tombstone; Testcontainers PostgreSQL; 44 tests |
| M2-2b | #27 | RabbitMQ listener (`notify.events` on `request.#`, DLQ), Redis de-dup marked after commit, 3 attempts then DLQ, health DOWN until bound; **api now waits for notify healthy in Compose**; 64 tests. M2-2 done |
| M4-3a | #28 | Pending list from `pending-for-me`, advisory triage summary, pull-to-refresh, request counter against stale responses; fixtures typed with `satisfies`; 50 tests |
| M4-3b | #29 | Overdue badge (requestId + stepOrder), any overdue failure silent; **only the API's 401 ends the session**; sign-out header button with one retry and a notice if the clear fails; 63 tests |
| M2-3 | #30 | `GET /notify/overdue` scoped by role; `dueAt = pendingSince + NOTIFY_STEP_SLA` at query time; strict ISO-8601 SLA (startup fails on `24`, `24h`); 88 tests. Job moved to M2-4 |
| M3-3a | #31 | Agent loop behind `LlmClient` with a fake LLM: ≤ 4 read-only tool rounds, one tool-less final call, strict `TriageSuggestionInput`, typed `post_triage` as the only write; no model/request text in logs; markers can't be rebuilt; 118 tests |

**As of 2026-11-18 (late), fourth round open for review.** Three PRs wait for Ahmed; nothing merged since `e670cbb` (PR #32). Each passed the reviewer subagent with no blocking finding.

| Task | PR | Result |
|---|---|---|
| M4-4a | #33 | Read-only detail: generated `getServiceRequest`, audit trail in API order, advisory triage block, plain errors, 401 left to the interceptor, stale-response and id-change guards; 77 tests. Not run on a device |
| M3-3b | #34 | `GeminiClient` (google-genai 3.5.0): read-only tool declarations, AFC off, sanitised JSON schema for the final call, 429/5xx/transport retryable, blank key = no key, `app.gemini_smoke`; Compose and `.env.example` gain `GEMINI_API_KEY`/`GEMINI_MODEL`; stack healthy with an empty key; 153 tests. **Live call not yet run (no key in `.env`)** |
| M2-4a | #35 | `POST /notify/devices` (hand-checked body; roles from the JWT), V3 `device`, `PushSender` + Expo `RestClient` adapter, push only when the projection row changed (commit, push, Redis mark), Admin devices for unassigned steps; Compose gains `NOTIFY_PUSH_ENABLED`/`EXPO_PUSH_URL`; 135 tests. Lead's Compose check: register/re-register/400/403/401 as expected; a submit pushed to `it.manager`, Expo rejected the fake token and the device was deleted |

**Dependencies approved by Ahmed (2026-11-18):**
- notification-service: Spring Boot 4.1.2 starters (web, security, oauth2-resource-server, actuator, jdbc, flyway) with `flyway-database-postgresql` and `postgresql`; test starters. Testcontainers deferred to M2-2.
- triage-copilot: `fastapi==0.164.0`, `uvicorn==0.54.0`, `httpx==0.28.1`, `pydantic-settings==2.16.3`; dev `pytest==9.2.0` (anyio plugin and `httpx.MockTransport` instead of pytest-asyncio and respx).
- mobile: Expo 58 / RN 0.88.1 / React 19.3.1, React Navigation 7 (native-stack), screens, safe-area-context, expo-secure-store; dev typescript 6.0.3, jest, jest-expo, Testing Library, `@hey-api/openapi-ts` 0.99.0. No web packages. No mock server: M4-2 tests mock `fetch`, and the smoke script hits the real API.
- notification-service (M2-2): `spring-boot-starter-amqp`, `spring-boot-starter-data-redis` (Boot BOM 4.1.2); test `testcontainers-postgresql`, `testcontainers-junit-jupiter`, `testcontainers-rabbitmq` 2.0.5, `spring-boot-testcontainers`. Redis tests use a plain `redis:7.4-alpine` container.
- triage-copilot (M3-3b, M3-3c), approved 2026-11-18 (second session): `google-genai==3.5.0`, `aio-pika==10.2.3`, `redis==8.1.0`. Model id comes from `GEMINI_MODEL` in `.env`; Ahmed left the choice open ("doesn't matter now"), so the settings default is `gemini-2.5-flash`.

**Parallel phase mechanics:** one git worktree per service under `.worktrees/` (ignored via `.git/info/exclude`), reused across that service's tasks. Builders don't touch Compose; the lead adds each service and resolves the Compose conflicts when two services land at once.

**How the loop runs now** (see ORCHESTRATION.md for the full loop):
- Lead briefs a fresh builder per task, plan only. Lead approves plans when no new dependency, contract change or rule exception is involved; otherwise Ahmed decides.
- Lead verifies every task in compose against real PostgreSQL/RabbitMQ with seeded users, and validates JSON with ajv. OpenAPI component schemas are converted to JSON Schema by a short script (wrap `components.schemas` as `$defs`, rewrite `#/components/schemas/` to `#/$defs/` and `../events/defs.schema.json` to that file's `$id`, pass it with `-r`). Run one negative case (drop a required key) so a pass means something; `jsonschema` is not installed.
- Migrations that touch existing rows are checked by starting `main` in a `git worktree` under the same compose project name, writing data, then rebuilding only `api` from the branch.
- Seed data never reaches the Admin fallback (no workflow has a GeneralManager step). Compose checks switch the Leave step to GeneralManager in the throwaway dev DB and wipe it with `docker compose down -v` afterwards.
- Size: builders' test estimates run 1.5 to 2 times low (see the M1-2a to M1-4b row below). Split at plan time.

**Decided 2026-11-18**
- **Approval tiers:** deferred until after M6 (Ahmed). The model and its open questions are in `docs/design/approval-hierarchy.md`; it is not a PLAN.md task.
- **`position` is a plain string** in the contracts (Ahmed, Gate A re-opened for this change; PR #16). It has a pattern and `maxLength` 30 (the API's column width), and today's five values are kept as `examples`. Consumer rule 4 in `contracts/events/README.md` says to treat it as an opaque label. Every M2/M3/M4 brief carries rule 4.
- **LLM provider: Google Gemini**, as a trial (Ahmed). One provider only (CLAUDE.md). Key from `GEMINI_API_KEY` (not yet in `.env.example`; add with M3-3b); with no key the copilot starts and skips triage.
- **Due time (lead, M2-3; the contract had no rule):** one configurable `NOTIFY_STEP_SLA` (strict ISO-8601, default `PT24H`, `PT1M` for demos), no per-position SLAs. The scheduled "newly overdue" job moved to M2-4, its only consumer (cut-list item 3).
- **Session (lead, M4-3b):** only the API's 401 ends the mobile session; the notification client has no interceptor (the API issues tokens, rule 7, and every list load also calls the API). The M4-5 builder must not add it back for `registerDevice`.
- **Copilot readiness (lead, M3-3):** no `/ready` endpoint (it isn't in the contract). In M3-3c the consumer writes a marker file after `queue.bind` and a Redis ping succeed; the image HEALTHCHECK requires the marker and `/health` 200, and the api then waits on the copilot in Compose like it does on notify.

**Open decisions for Ahmed**
- Review and merge PRs #33, #34, #35 (independent; any order).
- Put a `GEMINI_API_KEY` in `.env` (root and `.worktrees/copilot/.env`) so the lead can run `python -m app.gemini_smoke` (Docker form in `docs/handoffs/M3-3b.md`). Until then, which schema keywords Gemini accepts is assumed.

**Decided 2026-11-18 (late, lead)**
- M4-4b buttons: Approve/Reject show only when status is `Submitted` or `InReview` and a step with `decision` `Pending` exists; the server stays the authority (403/409). Reject sends a required `reason`, approve an optional `comment` (contract). 403 text names no specific reason. A `refresh?: number` param on PendingApprovals reloads the list after a decision; a 409 refetches the detail.
- M2-4: split into 4a (above) and 4b. Push is at most once (a crash between commit and push loses it); roles are a registration-time snapshot.

**Next (plan-only first; one builder per service)**
- **M2-4b:** V4 migration `overdue_since` on `pending_step` (reset to NULL when the step changes), `@Scheduled` job claiming newly overdue steps with one atomic `UPDATE … RETURNING` (safe across overlapping runs), push through M2-4a's `PushNotifier`; `NOTIFY_OVERDUE_JOB_INTERVAL` (ISO, default `PT1M`) for the lead to add to Compose. Consider a longer Expo connect timeout (one cold call timed out at 5 s in the Compose check).
- **M4-4b:** approve and reject as decided above; plain 403/409/400/404/network messages; in-flight guard. Acceptance: approving on the phone moves the request on.
- **M3-3c:** aio-pika consumer, queue `copilot.triage` on `request.submitted` with DLQ `copilot.triage.dlq`, Redis `processed:copilot:{eventId}` marked after the post, 3 attempts then DLQ, readiness marker, FastAPI `lifespan` owns client, consumer and Redis. Never log `str(exc)` for model errors (M3-3b keeps the provider's message on non-retryable `APIError`); log the failure reason class once retries run out. Lead: Compose env, api waits for copilot healthy.
- Then M3-4 (evals; Gate B: Ahmed owns the 50 labelled cases), M3-5 (guardrails, canned mode), M4-5 (push on the phone), M5.

**Resuming in a new session**
- Builders are fresh agents per session; the previous session's builder agents are gone. Brief from this summary and the task's hand-offs.
- Worktrees still exist locally under `.worktrees/` (`notify`, `copilot`, `mobile`), on the PR #35, #34 and #33 branches; a `.env` copy sits in `notify` and `copilot` (ignored). Before each task: `git -C .worktrees/<svc> switch -c <task-branch> origin/main` after checking `git status` is clean. Remove them with `git worktree remove` when the parallel phase ends.
- Lead's verification helpers live in the scratchpad and are gone with the session; recreate as needed: a Python 3.12 test runner (`docker run -v "$PWD":/repo -w /repo/triage-copilot python:3.12-slim …`), a Compose check under its own project name (`docker compose -p sdverify up --build -d --wait` from a worktree, `down -v` after), the OpenAPI-to-JSON-Schema converter (see "How the loop runs now"), and the demo-account login loop (password `ServiceDesk!2026`, seeded in Development only).

**Open follow-ups**
- **M3-3:** the M3-3a hand-off lists the copilot follow-ups: snake_case `suggested_route` mislabelled as an extra field in the reason (cosmetic); fullwidth look-alike angle brackets are not collapsed (low value). Starlette's `httpx2` deprecation warning is left alone (a new dependency).
- **Notify:** a body with no `version` is acked as unsupported rather than dead-lettered; a committed event can be dead-lettered if Redis stays down (replay is a no-op); nothing reads the DLQs yet.
- **Mobile:** whether Hermes on RN 0.88 provides `Intl.RelativeTimeFormat` is unconfirmed (an absolute-date fallback exists); `configureApi` runs during render (harmless); nothing has run on a device yet.
- **Mobile:** `npm audit` reports picomatch/minimatch advisories in Expo/Metro build tooling. The only fix offered is a downgrade, so revisit with the next Expo SDK. Replace the placeholder bundle id `dev.servicedesk.mobile` before any store or EAS build. `.env.example`'s proxy URL is a placeholder until M5.
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
| 2026-11-18 | contracts (position) | Lead tacked a `git checkout -- .` "cleanup" onto a validation command, on a branch whose only work was uncommitted contract edits; it would have discarded them | Claude Code's auto-mode permission check denied the command | Never chain a discard command onto a check. Scratch files live in the scratchpad, so there is nothing in the tree to clean up |
| 2026-11-18 | contracts (position) | Lead changed `approverPosition` in `defs.schema.json` and ran only the contract checks. `OutboxEventFactoryTests` reads that enum from the schema, so 6 API tests failed in CI. The lead's search for schema readers had found the test, but `head -10` cut the output before it. The first draft also allowed `maxLength` 50 while the API's columns are `varchar(30)` | PR #16 CI (Build and test); the length while writing the design note | Any change under `contracts/` also runs `dotnet test` (the API tests read the contract files). Never truncate a search whose purpose is finding every use. Test now reads `examples` and checks the pattern; a mutation (dropping `ItManager` from `examples`) fails 3 cases |
| 2026-11-18 | M3-1 | copilot-python stored the client secret as a plain `str`; pydantic printed it in a missing-field `ValidationError` and in `repr(Settings)`. `SecretStr` alone still leaked it via the error's input dict | Reviewer (reproduced it), then the builder found the second leak while fixing | `SecretStr` plus `hide_input_in_errors=True`, with tests that the value never appears in `str`/`repr` of errors or settings |
| 2026-11-18 | M2-1 | notify-spring set `management.endpoints.web.exposure.include: ""` believing it turned Actuator off; Boot treats it as unset, so `/actuator/health` answered any valid token. A 16-byte JWT key also started "healthy" and then rejected every request. The lead's compose check only probed `/notify/health` | Reviewer (ran the app and probed `/actuator`) | `/actuator/**` denied in `SecurityConfig` with a test; key length checked at startup. Lead's compose checks now probe the framework's default endpoints too, not only the ones the contract names |
| 2026-11-18 | M4-1a, M4-1b | mobile-rn removed `~` ranges from `package.json` by hand without regenerating the lockfile; in M4-1b a "missing base URL" test read `process.env`, so it failed whenever the variable was exported | Reviewer, both times | Lockfile regenerated and proven with `rm -rf node_modules && npm ci`; tests that depend on env clear it themselves. Briefs now say `npm install --save-exact` |
| 2026-11-18 | M2-2b | notify-spring used `spring.rabbitmq.listener.simple.retry.max-attempts`, which Boot 4.1 silently ignores; the default gave 4 attempts, not 3 | The builder's own test asserting exactly 3 calls | `max-retries: 2`, pinned by tests. Retry and similar "silently ignored" settings get a test that counts the effect |
| 2026-11-18 | M3-2b | The contract-reading test skipped when `contracts/` was missing, and the documented copilot test command mounted only `triage-copilot/`, so the test never ran by default | Lead running the documented command and seeing "1 skipped" | Tests that read shared files fail, not skip; commands mount the whole repo. Same rule applied in notify's contract test (M2-3) |
| 2026-11-18 | M4-2a, M4-3a | mobile-rn wrote a double-tap test that passed with the guard removed, and fixtures with enum values the contract forbids (`'Software'`, `'Department'`) while the hand-off called them contract-shaped | Reviewer (ran mutations; compared fixtures to the contract) | Every guard gets a "remove it, see a test fail" check recorded in the hand-off; fixtures are typed with `satisfies` against the generated types so tsc rejects bad values |
| 2026-11-18 | M4-2b, M4-3b | Two session holes: a slow 401 from an old token could log out a newly signed-in session, and a failing token clear on sign-out left the token stored while the user saw Login | Reviewer, both times | A 401 ends the session only if its bearer token is the stored one; sign-out retries the clear and tells the user if it still fails |
| 2026-11-18 | M2-3 | Spring's `Duration` binding accepted `NOTIFY_STEP_SLA=24` as 24 ms (every step instantly overdue). After the fix, the builder chained the parse error, and Spring's startup report showed only the deepest cause, hiding the message | Reviewer (binding check), then the lead starting the real image in Compose with `24` | Bind as `String`, `Duration.parse`, no chained cause. Check startup failures by starting the image against real dependencies, not with a fake DB host (the DB error hid the SLA error in the lead's first attempt) |
| 2026-11-18 | M3-3a | copilot-python built the invalid-output reason from pydantic error locations; with `extra="forbid"` the location is the model-chosen key, so a steered model could write PII or fake log lines (newlines included) into logs | Reviewer (reproduced it) | Reasons name only known fields and error types. The lead's later check also caught the marker defang rewriting normal text ("request data"); the angle-bracket collapse alone is enough |
| 2026-11-18 | lead | The M2-2b builder hit the 5-hour usage limit mid-task; its files were intact and it resumed after the reset | Task notification | Resume the same agent with SendMessage after the reset; check the worktree first |
| 2026-11-18 | M4-4 | Lead's brief said approve and reject each take "a comment"; the contract has an optional `comment` on approve and a required `reason` on reject | mobile-rn builder reading `servicedesk-api.yaml` at plan time | Briefs quote request bodies from the contract instead of paraphrasing them |
