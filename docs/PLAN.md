# Build plan

Fourteen working days, part-time. Each task is sized for one agent session and one review. Tick a task only after its acceptance check passes and the diff has been read.

Owner column: `lead` is the main session; the others are the subagents in `.claude/agents/`.

## Milestone 0: Foundation (day 1)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M0-1 | Create the repo layout; copy the existing C# project into `servicedesk-api/` | lead | `dotnet test ServiceDesk.slnx` passes from the new location |
| M0-2 | Write `contracts/events/*.schema.json` for the envelope and the six events | lead | Each schema validates its example file |
| M0-3 | Write `contracts/openapi/` for the new endpoints of all three services | lead | Files lint cleanly; Ahmed approves the contracts |
| M0-4 | `docker-compose.yml` with SQL Server, PostgreSQL, RabbitMQ, Redis and the C# API | lead | `docker compose up` starts; `/health` returns Healthy |

**Human gate A:** contracts are frozen after M0-3. Later changes to `contracts/` need Ahmed's approval.

## Milestone 1: Events out of the C# API (days 1 to 2)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M1-1 | Outbox table and writing an outbox row in the same transaction as each state change | api-dotnet | Unit tests show one outbox row per transition, none on a failed transition |
| M1-2 | Background publisher that sends outbox rows to RabbitMQ and marks them sent | api-dotnet | Submitting a request puts a schema-valid `request.submitted` message on the exchange |
| M1-3 | `GET /api/service-requests/pending-for-me` | api-dotnet | Returns only steps the caller may decide; tests cover scope and escalation |
| M1-4 | `POST /api/service-requests/{id}/triage` for a service role only | api-dotnet | A user token gets 403; the service token stores the suggestion; no state change occurs |

## Milestone 2: Notification and SLA service (days 3 to 5)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M2-1 | Spring Boot skeleton, PostgreSQL migrations, JWT validation, health endpoint | notify-spring | `./mvnw test` passes; service starts in Compose |
| M2-2 | Consumers for the six events with Redis de-duplication | notify-spring | Replaying the same event twice changes the tables once |
| M2-3 | Pending-step table with due times; scheduled overdue job | notify-spring | A step past its due time appears in `GET /notify/overdue` |
| M2-4 | `POST /notify/devices` and push sending through Expo (behind an interface, faked in tests) | notify-spring | Test shows the right approver is notified on `request.submitted` |

## Milestone 3: Triage copilot (days 6 to 8)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M3-1 | FastAPI skeleton, service-token client for the C# API, health endpoint | copilot-python | `pytest` passes; service starts in Compose |
| M3-2 | Tools: get request, get workflow template, get requester history | copilot-python | Tool functions tested against a stubbed API |
| M3-3 | Agent loop: on `request.submitted`, call the LLM with tools and post the triage suggestion | copilot-python | A submitted request gets a suggestion within the demo flow |
| M3-4 | Eval set of 50 labelled requests in `evals/cases.jsonl` and `python -m evals.run` | copilot-python | Script prints category and priority accuracy; result recorded in the README |
| M3-5 | Guardrails: output schema validation, low-confidence fallback, rate limit, canned mode for the public demo | copilot-python | Invalid model output never reaches the API; canned mode needs no API key |

**Human gate B:** Ahmed labels or checks the 50 eval cases himself. An eval set the agent both wrote and graded proves nothing.

## Milestone 4: Mobile app (days 9 to 11)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M4-1 | Expo project, navigation, API client generated from the OpenAPI files, token storage | mobile-rn | App builds and reaches the login screen |
| M4-2 | Login screen | mobile-rn | Demo accounts can sign in; wrong password shows an error |
| M4-3 | Pending approvals list with overdue badge and copilot summary | mobile-rn | List matches `pending-for-me` for the signed-in user |
| M4-4 | Request detail with audit trail, approve and reject with comment | mobile-rn | Approving on the phone moves the request to the next step; 403 and 409 show clear messages |
| M4-5 | Register the device for push and handle an incoming notification | mobile-rn | Submitting a request raises a notification on the approver's phone |

## Milestone 5: Integration (day 12)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M5-1 | Reverse proxy config; all services behind one address | lead | The phone uses a single base URL |
| M5-2 | End-to-end script: submit, triage, approve twice, fulfil | lead | Script passes against a fresh `docker compose up --build` |

**Human gate C:** Ahmed runs the full flow by hand on a real phone before anything is recorded.

## Milestone 6: Ship (days 13 to 14)

| ID | Task | Owner | Acceptance check |
|---|---|---|---|
| M6-1 | README: what it does, architecture diagram, two-minute quick start, eval result, known limits | lead | A stranger can run the stack from the README alone |
| M6-2 | 60-second demo recording | Ahmed | Shows submit, copilot triage, phone approval, notification |
| M6-3 | Turn `docs/build-log.md` into a short "what the agents got wrong" write-up | Ahmed | Ready to post |
| M6-4 | Deploy the Compose stack to one small server with the copilot in canned or rate-limited mode | lead | Public Swagger link works; README says "deployed demo" |

## If time runs short, cut in this order

1. M6-4 deployment (do it the week after)
2. M4-5 push on the phone (keep the overdue list)
3. M2-3 overdue job
4. M3-2 requester-history tool

Never cut: the outbox, the eval set, the demo recording.
