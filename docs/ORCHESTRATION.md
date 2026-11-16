# Multi-agent orchestration

How the agents in this repo are organised, when they run in parallel, and where a human decides.

## Principle

Agents are fast at writing code inside a clear boundary and unreliable at keeping several boundaries consistent. So the boundaries are fixed first, by a human, and each agent works inside exactly one.

## Roles

| Role | Who | Writes to | Job |
|---|---|---|---|
| Lead | Main Claude Code session | `contracts/`, `docker-compose.yml`, `proxy/`, `docs/`, root files | Breaks work into tasks, delegates, integrates, never writes service code |
| `api-dotnet` | Subagent | `servicedesk-api/` | C# API changes |
| `notify-spring` | Subagent | `notification-service/` | Spring Boot service |
| `copilot-python` | Subagent | `triage-copilot/` | FastAPI service, agent loop, evals |
| `mobile-rn` | Subagent | `mobile/` | React Native app |
| `reviewer` | Subagent, read-only | nothing | Reviews a finished task against the contracts and `CLAUDE.md` |
| Ahmed | Human | anything | Approves contracts, reads every diff, merges, owns the eval labels |

Two rules follow from the table:

- **One writer per folder.** No two agents ever edit the same folder, so parallel work cannot conflict.
- **The author never reviews.** The reviewer starts with a fresh context and has no write tools.

## The loop for one task

1. **Lead** picks the next unchecked task in `docs/PLAN.md` and writes a brief: task ID, goal, files in scope, contract files to obey, acceptance check.
2. **Builder** replies with a short plan (files, tests, verification). Lead or Ahmed approves it.
3. **Builder** writes tests, then code, runs the service's tests, and writes `docs/handoffs/<task-id>.md`.
4. **Reviewer** reads the diff, the hand-off and the relevant contract, runs the tests, and returns findings marked *blocking* or *note*.
5. **Builder** fixes blocking findings. One more review pass at most; after that it goes to Ahmed.
6. **Ahmed** reads the diff, merges, ticks the task.

A brief that the builder cannot act on without guessing is a bad brief. The lead rewrites it; the builder does not guess.

## Sequential or parallel

| Phase | Mode | Why |
|---|---|---|
| Milestone 0 (contracts, Compose) | Sequential, lead only | Everything else depends on it |
| Milestone 1 (events out of the C# API) | Sequential | Produces the real messages the others consume |
| Milestones 2, 3, 4 | Parallel, up to three builders | Each stays in its own folder and codes against the frozen contracts |
| Milestone 5 (integration) | Sequential, lead only | Cross-service problems need one mind |

During the parallel phase, each builder tests against the contract and not against the other services: the Spring and Python services consume example events from `contracts/events/examples/`, and the mobile app uses a mock server generated from the OpenAPI files. That is what makes parallel work safe.

If parallel sessions start producing confusion or rework, drop back to one builder at a time. On a two-week solo build, three sessions you cannot review are slower than one you can.

Optional: run parallel builders in separate git worktrees (`isolation: worktree` in the subagent file) so each has its own checkout. It is not required while the one-writer-per-folder rule holds.

## Contract changes

A builder that needs a contract change stops and reports: what is missing, the smallest change that would fix it, and which other services it affects. The lead makes the change, Ahmed approves it, and the lead notifies every affected builder in its next brief. No builder edits `contracts/`.

## Hand-offs

Agents do not share memory. Everything one session needs to pass on goes in `docs/handoffs/<task-id>.md` using the template. The lead reads the hand-off, not the session transcript, before writing the next brief.

## Human gates

| Gate | When | What Ahmed checks |
|---|---|---|
| A | After contracts are written | Event names, payloads and endpoints are what the product needs |
| B | Before the eval result is published | The 50 eval cases and their labels are his, not the agent's |
| C | Before recording | The whole flow works by hand on a real phone |
| Every task | Before merge | The diff, with extra attention to whatever the hand-off flags |

## Stop conditions

An agent stops and asks when:

- the same problem has failed twice;
- the task needs a file outside its folder;
- an architecture rule in `CLAUDE.md` would have to be broken;
- a test would have to be weakened or removed;
- a new dependency, a schema change or a secret is involved.

## What gets recorded

`docs/build-log.md` collects every case where an agent was wrong and a check caught it: which check, how long it took to notice, what rule was added afterwards. It is the raw material for the interview answer and the write-up, so keep it honest and specific.

## Using this with other tools

`CLAUDE.md` and `.claude/agents/` are read by Claude Code. If you also work in Cursor or Antigravity, copy `CLAUDE.md` to `AGENTS.md` so they pick up the same rules, and keep the two files identical.
