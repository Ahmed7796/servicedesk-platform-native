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

## Transport (RabbitMQ)

| Item | Value |
|---|---|
| Exchange | `servicedesk.events`, type `topic`, durable. `servicedesk-api` declares it on each new broker connection, so it may not exist until the first event is published. **Every consumer must declare it too** (same arguments, so the declaration is idempotent) before binding its queue |
| Routing key | The event's `type`, e.g. `request.submitted` |
| Body | The envelope JSON, UTF-8, exactly as stored in the outbox |
| `content_type` | `application/json` |
| `message_id` | The envelope's `eventId` |
| `type` | The envelope's `type` |
| `delivery_mode` | `2` (persistent) |

**Delivery.** At least once. The publisher marks an outbox row sent only after the broker confirms it (publisher confirms), so a crash between publish and confirm sends the message again. This is why consumers de-duplicate (rule 1 below).

**Order.** Rows are published oldest first (`occurredAt`, then `eventId`), but consumers must not rely on order across requests, and a retry can deliver an older event after a newer one. Use `occurredAt` when order matters.

**Queues belong to consumers.** Each consuming service declares its own durable queue and binds the routing keys it needs, e.g. `notify.events` bound to `request.#`, `copilot.triage` bound to `request.submitted`. The publisher never declares queues. A message published while no queue is bound is dropped by the broker, and the outbox does not resend it, because the broker confirmed it. So each consumer declares and binds its queue at startup, before it reports healthy, and in Compose the API waits for the consumers' health checks (`depends_on: condition: service_healthy`, added when M2-1 and M3-1 add those services). Before then, events with no bound queue are lost; the demo accepts this. Virtual host: the default, `/`.

## Rules for consumers

1. **De-duplicate on `eventId`.** Delivery is at-least-once. Record processed IDs in Redis and ignore repeats (CLAUDE.md, rule 5).
2. **Ignore unknown fields.** Consumers must accept and ignore fields they do not recognise, at any level of the message. This makes adding an optional field a non-breaking change that does not bump `version`. Do not configure deserialisers to fail on unknown properties (in Spring/Jackson, keep `FAIL_ON_UNKNOWN_PROPERTIES` off; in Pydantic, do not use `extra="forbid"` on event models).
3. **Check `type` and `version`.** Handle the versions you know; log and skip any others.
4. **Treat `position` as an opaque label.** It is a plain string, not an enum (`defs.schema.json`, `approverPosition`). Today's values are `DepartmentHead`, `ItManager`, `HrManager`, `FinanceManager` and `GeneralManager`, but a new value is not a breaking change and does not bump `version`. Do not map it to a closed enum that fails on an unknown value (in Java, keep it a `String`; in Pydantic, a `str`). Any per-position behaviour, such as a display name, needs a fallback for unknown values, e.g. showing the raw string.

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
