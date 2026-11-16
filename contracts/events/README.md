# Event contracts

The source of truth for messages that `servicedesk-api` publishes to RabbitMQ. Change these files first, then the code (CLAUDE.md, rule 4). Contracts are frozen at Gate A (after M0-3); after that, changes need Ahmed's approval.

## Files

| File | What it is |
|---|---|
| `envelope.schema.json` | Fields every event has: `eventId, type, occurredAt, requestId, actorId, payload, version` |
| `defs.schema.json` | Shared types: uuid, enums, step shapes |
| `request.*.schema.json` | One schema per event type; narrows `type`, `version` and `payload` |
| `examples/*.json` | One valid example per event. Spring and Python builders test against these |
| `examples/invalid/*.bad-<format>.json` | Must fail, on that format. Proves formats are enforced |
| `../validate.sh` | Runs all of the above. Needs Node; fetches `ajv-cli@5.0.0` and `ajv-formats@3.0.1` through `npx` |

JSON Schema draft 2020-12. Enum values are the C# enum names (the API already serialises enums as strings).

## Events

All events are published only by `servicedesk-api`, through the outbox, in the same transaction as the state change.

| Type | Raised by | Payload (besides `requesterId`, `status`) | Main consumers |
|---|---|---|---|
| `request.submitted` | `ServiceRequest.Submit` | `title`, `requestType`, `departmentId`, `steps[]`, `currentStep` (with `approverUserIds`) | copilot (triage), notify (first approver) |
| `request.step_approved` | `Approve`, a later step still pending | `decidedStep`, `comment`, `nextStep` (with `approverUserIds`) | notify (next approver) |
| `request.approved` | `Approve`, last step | `decidedStep`, `comment` | notify (requester, admins) |
| `request.rejected` | `Reject` | `decidedStep`, `reason` | notify (requester; clear pending step) |
| `request.cancelled` | `Cancel` | `previousStatus` | notify (clear pending step) |
| `request.fulfilled` | `Fulfil` | none | notify (requester) |

Escalation (a step moved to GeneralManager because the requester holds its position) happens inside `Submit`. It is shown by `escalatedFrom` on the steps of `request.submitted`, not by a separate event.

`occurredAt` is when the state change happened, not when the publisher sent it. `actorId` is the user whose action caused the event; for an Admin deciding on someone's behalf it is the Admin.

## Rules for consumers

1. **De-duplicate on `eventId`.** Delivery is at-least-once. Record processed IDs in Redis and ignore repeats (CLAUDE.md, rule 5).
2. **Ignore unknown fields.** Consumers must accept and ignore fields they do not recognise, at any level of the message. This makes adding an optional field a non-breaking change that does not bump `version`. Do not configure deserialisers to fail on unknown properties (in Spring/Jackson, keep `FAIL_ON_UNKNOWN_PROPERTIES` off; in Pydantic, do not use `extra="forbid"` on event models).
3. **Check `type` and `version`.** Handle the versions you know; log and skip any others.

The schemas themselves use `additionalProperties: false`. They are strict for the **producer**, so that a misspelled field fails validation in the API's tests. Rule 2 is about **consumers**, which must be lenient.

## Versioning

- Adding an optional field: update the schema and examples; `version` stays the same. Consumers keep working because of rule 2.
- Renaming or removing a field, changing a type, or making a field required: a breaking change. Bump `version` for that event and have the API publish both versions until every consumer has moved.

## `approverUserIds`

`currentStep` in `request.submitted` and `nextStep` in `request.step_approved` carry `approverUserIds`: the users the API resolved as eligible to decide that step **at publish time**. The API computes it with `GetEligibleApproversAsync` (PLAN.md task M1-0), which must agree with `ServiceRequest.EnsureCanDecideCurrentStep`:

Build the list in three steps:

1. **Position holders:** users with role Approver and the step's position, restricted to the request's department when the scope is `RequesterDepartment`, any department when it is `Global`.
2. **Plus Admins, when the Admin fallback applies:** if the request's `RequesterPosition` (snapshot at submission) equals the step's position, add every Admin. In practice this is a GeneralManager requester, whose step cannot be escalated further. The fallback adds to step 1; it does not replace it.
3. **Minus the requester:** nobody may decide their own request.

**Empty list.** `approverUserIds` may be empty, for example when a department has no DepartmentHead. A consumer that receives an empty list must:

- log a warning with `eventId`, `requestId` and the step's `order` and `position`; and
- show the step as **unassigned** to admins (for the notification service: record it as a pending step with no assignee and include it in what admins see).

It must not drop the event, and it must not fail silently.

## Known limits

- **`approverUserIds` is a snapshot.** It is resolved when the event is written to the outbox and is not updated if roles, positions or departments change afterwards. Approve/reject permission is always re-checked by the API at decision time, so a stale list can cause a missed or extra notification but never a wrong decision.
- Events carry no request `description`. The copilot reads the full request through the API's HTTP endpoints.
