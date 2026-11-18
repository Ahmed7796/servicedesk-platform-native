# Approval hierarchy: design note

**Status:** deferred until after Milestone 6 (Ahmed, 2026-11-18). Not in `docs/PLAN.md`; nothing here is built. Raised 2026-11-17.

## Why this note exists

The approval model is fixed in C# enums. It works for the demo's five positions and four workflows, but every organisational change is a code change. This note records the model we would move to, so it can be picked up after the build window without re-deriving it.

## How it works today

| Concept | Where | Behaviour |
|---|---|---|
| Role | `UserRole` enum: `Requester`, `Approver`, `Admin` | What a user may do at all |
| Position | `ApproverPosition` enum: `DepartmentHead`, `ItManager`, `HrManager`, `FinanceManager`, `GeneralManager` | Which steps an approver may decide |
| Scope | `ApprovalScope` enum: `RequesterDepartment`, `Global` | Whether a step is limited to the requester's department |
| Workflow | `WorkflowTemplate` per `RequestType`: an ordered list of (position, scope) | The steps a request goes through |
| Escalation | `EscalationPolicy.Escalate`: every position escalates to `GeneralManager`; `GeneralManager` escalates to nothing | When the requester holds a step's position, `Submit` moves that step to `GeneralManager` and writes a `StepEscalated` audit entry |
| Admin fallback | `ServiceRequest` (`RequesterPosition` snapshot) | When the requester holds the step's position and there is no one above (a GeneralManager requester on a GeneralManager step), any Admin may decide. The audit text says "on behalf" |

Limits of this model:

1. **A new position is a code change.** It needs a new enum value, a build, a deploy and EF value changes. The contract no longer needs a change: `approverPosition` became a plain string in PR #16.
2. **Escalation is one hard-coded rule.** Everything goes to `GeneralManager`. There is no "next level up" and no way to route Finance to a CFO.
3. **`GeneralManager` is a fixed top tier,** and the Admin fallback stands in for a tier above it. An Admin deciding a request is a normal approval with a note, not a separately audited act.
4. **No reporting line.** "Department head of the requester's department" is the only person-relative rule. "The requester's own manager" cannot be expressed.

## Proposed model

### 1. Positions as data, with a rank

A `Positions` table: `Key` (the string the contract already carries, e.g. `ItManager`), `DisplayName`, `Rank` (integer; higher is more senior), and an optional `EscalatesTo` (another position's key).

- `User.Position` and the workflow steps reference a position by key.
- Escalation is the explicit `EscalatesTo` if set, otherwise the lowest-ranked position above the current one. That reproduces today's rule from data: the four managers escalate to GeneralManager, the top rank.
- The existing five keys are kept, so stored data, events and the contract stay valid.
- The triage input check (`suggestedRoute`) changes from the C# enum to a lookup of known keys. The contract already says an unknown position gets 400.

### 2. Optional reporting-line steps

A workflow step can name a position (as today) or be a **reporting-line step** ("the requester's manager"). That needs `User.ManagerId`. If the requester has no manager, the step escalates as above. Existing workflows don't use this kind of step and behave exactly as now.

### 3. An audited Admin override instead of a final tier

Replace the implicit Admin fallback with an explicit **override**: an Admin may decide any pending step, must give a reason, and the action is recorded as its own audit action (e.g. `AdminOverride`) rather than as a normal approval. The top of the hierarchy is then simply the highest rank, and nothing sits above it implicitly.

## What it would touch

| Area | Change |
|---|---|
| Domain | Replace the `ApproverPosition` enum with a position key and reference data. Rewrite `EscalationPolicy` over ranks. Add the reporting-line step kind and the override action. Change `EnsureCanDecideCurrentStep` |
| Persistence | New migration: a `Positions` table seeded with today's five keys; positions stored as keys (already strings in PostgreSQL, `varchar(30)`, which is also the contract's `maxLength`); `User.ManagerId` |
| `ApproverResolver` / `pending-for-me` | Resolve by position key, by reporting line, and by the override rule. M1-0's exhaustive test against `EnsureCanDecideCurrentStep` must be redone |
| Contracts | No change for positions (PR #16). The override **does** need one: a new `AuditAction` value, and possibly an event. That is a Gate A change, and every consumer must handle it |
| Consumers | None for positions, if they follow consumer rule 4 in `contracts/events/README.md`. The notification service would need to know about overrides if they notify anyone |
| Admin UI | Managing positions needs a screen or an endpoint. The web frontend is out of scope, so for the demo the data would be seed-only |

## Why it is deferred

- **Scope:** it reshapes the domain and the resolver in the middle of the two-week window, while Milestones 2 to 4 consume what the API emits now.
- **Explainability:** Ahmed must be able to explain every line of the C# service. One stable model through the build is easier to defend than a half-migrated one.
- **Cheap to defer:** the contract part (positions as plain strings) is done now, while no consumer depends on the enum. The rest is internal to `servicedesk-api`, apart from the override's audit action.

## Open questions for when it is picked up

1. Does the override need its own event, or is an audit entry plus the usual `request.approved`/`request.rejected` enough?
2. Should rank ties be allowed (two positions at the same level), and how does escalation choose between them?
3. Is `ManagerId` set from seed data only, or does it need an admin endpoint?
4. Should a request in flight keep the hierarchy it was submitted under (snapshot, like `RequesterPosition` today), or follow later changes?
