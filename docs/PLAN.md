# Build plan

Fourteen working days, part-time. Each task is sized for one agent session and one review. Set a task's Status to `done` only after its acceptance check passes and the diff has been read.

Owner column: `lead` is the main session; the others are the subagents in `.claude/agents/`.

## Milestone 0: Foundation (day 1)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M0-1 | Create the repo layout; copy the existing C# project into `servicedesk-api/` | lead | `dotnet test ServiceDesk.slnx` passes from the new location | done |
| M0-2 | Write `contracts/events/*.schema.json` for the envelope and the six events | lead | Each schema validates its example file | done |
| M0-3 | Write `contracts/openapi/` for the new endpoints of all three services | lead | Files lint cleanly (`contracts/lint.sh`); Ahmed approves the contracts | done |
| M0-4 | `docker-compose.yml` with SQL Server, PostgreSQL, RabbitMQ, Redis and the C# API | lead | `docker compose up` starts; `/health` returns Healthy | done |
| M0-5 | Remove the hardcoded connection string (SA password) from `appsettings.Development.json`; local `dotnet run` reads `ConnectionStrings__Default` from the environment or `dotnet user-secrets` | api-dotnet | No password in any tracked file under `servicedesk-api/`; `dotnet run` without the variable fails with a message naming `ConnectionStrings__Default`; `dotnet test` passes; the API still starts in Compose | done |
| M0-6 | Move `servicedesk-api` from SQL Server to PostgreSQL (decided 2026-11-17, before M1-2): Npgsql provider, `xmin` concurrency token, migrations regenerated for PostgreSQL. The lead moves Compose, `.env.example` and CLAUDE.md to an API-owned PostgreSQL container | api-dotnet + lead | `dotnet test` passes; `docker compose up --build` starts with no SQL Server container and `/health` is Healthy; a submit and an approve write two outbox rows whose bodies pass `contracts/validate.sh`'s ajv check; two concurrent updates to one request return 409 | done |

**Human gate A:** contracts are frozen after M0-3. Later changes to `contracts/` need Ahmed's approval.

## Milestone 1: Events out of the C# API (days 1 to 2)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M1-0 | `GetEligibleApproversAsync` with tests proving it agrees with `EnsureCanDecideCurrentStep` (rules in `contracts/events/README.md`, section `approverUserIds`) | api-dotnet | For every seeded user and every step of every workflow, the user is in the result exactly when `EnsureCanDecideCurrentStep` accepts them; plus a test workflow with a GeneralManager step submitted by the GM, so the Admin fallback is exercised | done |
| M1-1a | `OutboxMessage` entity, `OutboxMessages` table and migration, and a factory that builds the six event envelopes. Depends on M1-0 (fills `approverUserIds`) | api-dotnet | Factory tests compare each event's JSON with `contracts/events/examples/` (same keys at every level, enum values from `defs.schema.json`, `version` 1); `InitialCreate` unchanged | done |
| M1-1b | Write an outbox row in the same transaction as each state change (`ServiceRequestService` uses the factory and `ApproverResolver`). Depends on M1-1a | api-dotnet | Unit tests show one outbox row per transition, none on a failed transition | done |
| M1-2a | Outbox dispatcher behind an `IEventBroker` interface (no RabbitMQ, no background loop yet): publish a batch of unsent rows, mark each sent only after the broker confirms, stop the batch when the broker is down, skip a rejected row without blocking the rest; the EF store query; RabbitMQ options with startup validation | api-dotnet | Unit tests with a fake broker show: none marked before confirm or on failure, a rejected row does not block rows behind it and is retried next batch, the batch limit holds; missing RabbitMQ settings fail with a message naming the variable | done |
| M1-2b | Background loop (`OutboxPublisherService`, backoff, cancellation; adds `Microsoft.Extensions.Hosting.Abstractions` 10.0.14), RabbitMQ adapter (`RabbitMQ.Client` 7.2.2, publisher confirms, exchange per `contracts/events/README.md` Transport), wiring, `--healthcheck` probe in the API binary. The lead adds RabbitMQ settings and the API health check to Compose. Depends on M1-2a | api-dotnet + lead | Submitting a request puts a schema-valid `request.submitted` message on the exchange with the contract's message properties; rows are published oldest first; a GeneralManager requester's event lists Admins in `approverUserIds`; a row written while RabbitMQ is down is delivered after it restarts; the `api` container reports healthy | done |
| M1-3 | `GET /api/service-requests/pending-for-me` | api-dotnet | Returns only steps the caller may decide; tests cover scope and escalation | done |
| M1-4a | `POST /api/auth/service-token` (client credentials from env, role `TriageService`) and role restrictions so that role can only read requests | api-dotnet | A `service-token` token can `GET` requests and gets 403 on submit, approve, reject, cancel, fulfil and `pending-for-me`; wrong client secret gets 401 | todo |
| M1-4b | `POST /api/service-requests/{id}/triage` for `TriageService` only; `triage` on detail and pending items. Depends on M1-3 and M1-4a | api-dotnet | A user token gets 403; a service token stores the suggestion and a second post replaces it; no state change, audit entry or event occurs | todo |
| M1-5 | `auditTrail` and `requesterName` on `GET /api/service-requests/{id}` (needed by M4-4). Depends on M1-4b (`triage` is a required field) | api-dotnet | Response validates against `ServiceRequestDetail` in `contracts/openapi/servicedesk-api.yaml`; entries are oldest first, with a stable tiebreak for entries written at the same instant (`Submit` writes `Submitted` and `StepEscalated` with one timestamp), and include escalation entries | todo |

## Milestone 2: Notification and SLA service (days 3 to 5)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M2-1 | Spring Boot skeleton, PostgreSQL migrations, JWT validation, health endpoint | notify-spring | `./mvnw test` passes; service starts in Compose | todo |
| M2-2 | Consumers for the six events with Redis de-duplication | notify-spring | Replaying the same event twice changes the tables once | todo |
| M2-3 | Pending-step table with due times; scheduled overdue job | notify-spring | A step past its due time appears in `GET /notify/overdue`; a `TriageService` token gets 403 | todo |
| M2-4 | `POST /notify/devices` and push sending through Expo (behind an interface, faked in tests) | notify-spring | Test shows the right approver is notified on `request.submitted`; a `TriageService` token gets 403 on `POST /notify/devices` | todo |

## Milestone 3: Triage copilot (days 6 to 8)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M3-1 | FastAPI skeleton, service-token client for the C# API, health endpoint | copilot-python | `pytest` passes; service starts in Compose | todo |
| M3-2 | Tools: get request, get requester history (`GET /api/service-requests` filtered by `requesterId` on the client). No workflow-template tool: the route is the request's own `steps` | copilot-python | Tool functions tested against a stubbed API | todo |
| M3-3 | Agent loop: on `request.submitted`, call the LLM with tools and post the triage suggestion | copilot-python | A submitted request gets a suggestion within the demo flow | todo |
| M3-4 | Eval set of 50 labelled requests in `evals/cases.jsonl` and `python -m evals.run` | copilot-python | Script prints category and priority accuracy; result recorded in the README | todo |
| M3-5 | Guardrails: output schema validation, low-confidence fallback, rate limit, canned mode for the public demo | copilot-python | Invalid model output never reaches the API; canned mode needs no API key | todo |

**Human gate B:** Ahmed labels or checks the 50 eval cases himself. An eval set the agent both wrote and graded proves nothing.

## Milestone 4: Mobile app (days 9 to 11)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M4-1 | Expo project, navigation, API client generated from the OpenAPI files, token storage | mobile-rn | App builds and reaches the login screen | todo |
| M4-2 | Login screen | mobile-rn | Demo accounts can sign in; wrong password shows an error | todo |
| M4-3 | Pending approvals list with overdue badge and copilot summary | mobile-rn | List matches `pending-for-me` for the signed-in user | todo |
| M4-4 | Request detail with audit trail, approve and reject with comment | mobile-rn | Approving on the phone moves the request to the next step; 403 and 409 show clear messages | todo |
| M4-5 | Register the device for push and handle an incoming notification | mobile-rn | Submitting a request raises a notification on the approver's phone | todo |

## Milestone 5: Integration (day 12)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M5-1 | Reverse proxy config; all services behind one address | lead | The phone uses a single base URL | todo |
| M5-2 | End-to-end script: submit, triage, approve twice, fulfil | lead | Script passes against a fresh `docker compose up --build` | todo |

**Human gate C:** Ahmed runs the full flow by hand on a real phone before anything is recorded.

## Milestone 6: Ship (days 13 to 14)

| ID | Task | Owner | Acceptance check | Status |
|---|---|---|---|---|
| M6-1 | README: what it does, architecture diagram, two-minute quick start, eval result, known limits | lead | A stranger can run the stack from the README alone | todo |
| M6-2 | 60-second demo recording | Ahmed | Shows submit, copilot triage, phone approval, notification | todo |
| M6-3 | Turn `docs/build-log.md` into a short "what the agents got wrong" write-up | Ahmed | Ready to post | todo |
| M6-4 | Deploy the Compose stack to one small server with the copilot in canned or rate-limited mode | lead | Public Swagger link works; README says "deployed demo" | todo |

## If time runs short, cut in this order

1. M6-4 deployment (do it the week after)
2. M4-5 push on the phone (keep the overdue list)
3. M2-3 overdue job
4. M3-2 requester-history tool

Never cut: the outbox, the eval set, the demo recording.
