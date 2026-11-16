# HTTP contracts

OpenAPI 3.1 descriptions of every endpoint another service, the phone or the end-to-end script calls. Change these first, then the code (CLAUDE.md, rule 4). Contracts are frozen at Gate A (after M0-3); after that, changes need Ahmed's approval.

| File | Service | Reached by the phone at |
|---|---|---|
| `servicedesk-api.yaml` | C# API (`servicedesk-api/`) | `/api` via the proxy |
| `notification-service.yaml` | Spring (`notification-service/`) | `/notify` via the proxy |
| `triage-copilot.yaml` | FastAPI (`triage-copilot/`) | not reachable; health only |

Lint with `../lint.sh` (Spectral 6.17.0, `spectral:oas` rules, fails on warnings). The `servers` ports for the Spring and Python services are placeholders until Compose assigns them.

## Shared types

Enums (`RequestType`, `RequestStatus`, `ApproverPosition`, `ApprovalScope`, `StepDecision`, `AuditAction`) are `$ref`s to `../events/defs.schema.json`, so the HTTP and event contracts cannot drift apart. They are the C# enum names, as strings. A code generator must resolve external refs (bundle first if it does not).

## These files, not `/openapi/v1.json`

The C# API also serves a generated document at `/openapi/v1.json` in Development. **It is not the contract and is currently wrong in places**: it types enums as integers (the API sends strings), and lists 200 for submit (the API returns 201 with `Location`). Generate clients from these files.

What was checked against the running API (M0-3): login, submit, list, get, approve, cancel, and the 400, 401, 403, 404 and 409 error bodies all validate against the schemas here.

## Auth

- Users sign in with `POST /api/auth/login` and get a JWT (`sub`, `email`, `name`, `role`).
- The copilot gets a token from `POST /api/auth/service-token` with a client ID and secret from the environment. Its role is `TriageService`: it may read requests and post triage. Every other operation, on both the API and notify, refuses it with 403.
- The notification service validates the same JWTs with the same signing key. It issues none.
- No token, or an invalid one: **401 with an empty body**.

## Errors

Errors are RFC 9457 problem details (`application/problem+json`). Clients show `detail` when present, else `title`. Two exceptions clients must handle:

- 401 for a missing or invalid token has an empty body. (Login's 401 for wrong credentials is ProblemDetails.)
- 403 has a ProblemDetails body when a domain rule refused (wrong position, wrong department, own request), and an **empty body** when the role check refused (fulfil without `Admin`, triage without `TriageService`).

Validation errors (400) may include `errors`. Keys are usually the property name as the API spells it (`Title`, not `title`), but a JSON path (`$.type`) or `dto` for unreadable input. Show the messages; don't rely on the keys.

The contract is stricter than the API in two places, which is harmless for clients: request bodies forbid unknown fields (the API ignores them), and `type` is required on submit (the API defaults a missing `type` to `Equipment`). Clients must follow the contract.

## New endpoints and the tasks that build them

| Endpoint / field | Task |
|---|---|
| `GET /api/service-requests/pending-for-me` | M1-3 |
| `POST /api/auth/service-token`, `TriageService` role restrictions | M1-4a |
| `POST /api/service-requests/{id}/triage`, `triage` on detail and pending items | M1-4b |
| `auditTrail` and `requesterName` on `GET /api/service-requests/{id}` | M1-5 |
| `POST /notify/devices` | M2-4 |
| `GET /notify/overdue` | M2-3 |
| `GET /notify/health`, `GET /health` (copilot) | M2-1, M3-1 |

## Triage suggestion

`category` (a `RequestType`), `priority` (`Low`, `Normal`, `High`, `Urgent`), `suggestedRoute` (approver positions, in order), `summary` (≤ 500 chars), `confidence` (0 to 1). One per request; a later post replaces it. Posting does not change the request's status or steps, writes no audit entry and publishes no event. These values were accepted "for now" at M0-3 and may be revisited before Gate A closes.
