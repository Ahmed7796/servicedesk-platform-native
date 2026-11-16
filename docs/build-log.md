# Build log

One entry each time an agent was wrong and something caught it. Keep entries short and factual.

| Date | Task | What the agent did | What caught it | Rule added afterwards |
|---|---|---|---|---|
| (earlier project) | ServiceDesk API | Wired the demo seeder into startup for every environment | Diff review | Seeding runs only in Development (now in CLAUDE.md) |
| 2026-11-16 | M0-2 | Lead proposed `approverUserIds` in the event contract although the API has no way to resolve approvers for a step (`IUserRepository` has only `GetByIdAsync` and `GetByEmailOrEmployeeNumberAsync`) | Ahmed's instruction to stop if the domain can't resolve it, checked before any schema was written | Contract fields the API must fill need a named task that produces them (added M1-0) |
| 2026-11-16 | M0-2 | Lead's first README draft described the Admin fallback as replacing position holders; `EnsureCanDecideCurrentStep` adds Admins and still accepts other holders of the position | Lead re-reading the README against the domain code before review | Resolution rules are stated as steps and M1-0 must test them against `EnsureCanDecideCurrentStep` for every user |
| 2026-11-16 | M0-2 | Lead wrote M1-0's acceptance check over seeded data only; no seeded workflow has a GeneralManager step, so the Admin fallback would never be tested | Reviewer subagent | Acceptance checks for permission rules must name a case that reaches each branch |
| 2026-11-16 | M0-3 | Lead made `TriageSuggestion` extend `TriageSuggestionInput`, which had `additionalProperties: false`, so the response's own fields would have been rejected | Lead re-reading the schema before lint | Request schemas that forbid extra fields are never reused as bases; shared fields go in a separate base schema |
| 2026-11-16 | M0-3 | Lead's contract said every 403 has a ProblemDetails body; the API returns an empty body when `[Authorize(Roles=...)]` refuses | Validating live API responses against the contract | Contracts for existing endpoints are checked against the running API, not only against the code |
| 2026-11-16 | M0-3 | Lead's contract said the copilot's role "cannot" act but gave no 403 on approve/reject/cancel, and M1-4's check never tested a service token against them; today the refusal would only happen by accident (non-GUID `sub` → 401) | Reviewer subagent | Every rule in CLAUDE.md's architecture list that a task touches needs a negative test named in the acceptance check |
