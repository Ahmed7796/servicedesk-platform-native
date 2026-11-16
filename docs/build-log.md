# Build log

One entry each time an agent was wrong and something caught it. Keep entries short and factual.

## Progress summary

Updated by the lead at each milestone boundary, or when a long session is handed on, so the next session can start from here rather than from a transcript. The table of agent mistakes follows below.

**As of 2026-11-16, end of Milestone 0.** All Milestone 0 tasks are done (M0-5 closes with PR #4).

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

**Open follow-ups**
- An API container health check, with M1-2 (see `docs/handoffs/M0-4.md`).
- The generated `/openapi/v1.json` types enums as integers (api-dotnet; no task yet).
- `.env.example` needs the triage client ID and secret with the M1-4a brief.
- `validate.sh`, `lint.sh` and `docker compose up` don't run in CI.
- `ASPNETCORE_ENVIRONMENT=Development` in Compose must be revisited before M6-4.
- `dotnet ef` and local `dotnet run` now need `ConnectionStrings__Default` or user-secrets (a README line in M6-1).
- Consider a `.gitattributes` to pin line endings.

**Next:** Milestone 1 in order: M1-0, M1-1, M1-2, M1-3, M1-4a, M1-4b, M1-5. All are owned by api-dotnet and run one at a time (ORCHESTRATION.md).

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
