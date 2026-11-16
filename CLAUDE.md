# ServiceDesk Platform

A service-request system built as four small services. Employees submit requests, approvers act on them from a phone, and an LLM copilot suggests how each request should be triaged.

Owner: Ahmed Abdelgadir. This is a portfolio project with a two-week build window. Scope discipline matters more than features.

## Repo layout

```
servicedesk-api/        C# / ASP.NET Core 10, EF Core, SQL Server   (existing, extended here)
notification-service/   Java 21 / Spring Boot, PostgreSQL           (new)
triage-copilot/         Python 3.12 / FastAPI                        (new)
mobile/                 React Native (Expo), TypeScript              (new)
contracts/              events/*.schema.json, openapi/*.yaml         (source of truth between services)
proxy/                  reverse proxy config
docs/                   PLAN.md tasks, ORCHESTRATION.md, handoffs/, build-log.md
docker-compose.yml      whole stack: services, SQL Server, PostgreSQL, RabbitMQ, Redis
```

## Commands

| Where | Test | Run |
|---|---|---|
| `servicedesk-api/` | `dotnet test ServiceDesk.slnx` | `dotnet run --project src/ServiceDesk.Api` |
| `notification-service/` | `./mvnw test` | `./mvnw spring-boot:run` |
| `triage-copilot/` | `pytest` | `uvicorn app.main:app --reload` |
| `triage-copilot/` evals | `python -m evals.run` | prints accuracy over `evals/cases.jsonl` |
| `mobile/` | `npm test` | `npx expo start` |
| `contracts/` | `./validate.sh` and `./lint.sh` | event examples vs schemas; OpenAPI lint |
| repo root | `docker compose up --build` | whole stack |

Run the tests for the service you touched before you say a task is done. If a command above is wrong for the current state of the repo, fix this table in the same change.

## Architecture rules

These are decisions, not suggestions. If a task seems to need breaking one, stop and ask.

1. **Only `servicedesk-api` changes a request.** Other services read through its HTTP API and never touch its database.
2. **Each service owns its own database.** No shared tables, no cross-database queries.
3. **State changes publish events through an outbox.** The outbox row is written in the same transaction as the state change and the audit entry; a background publisher sends it to RabbitMQ.
4. **Events follow `contracts/events/`.** Every event has the envelope `eventId, type, occurredAt, requestId, actorId, payload, version`. Change the schema first, then the code.
5. **Consumers are idempotent.** Record processed `eventId`s in Redis and ignore repeats.
6. **The copilot is advisory.** It may read requests and post a triage suggestion (category, priority, suggested route, summary, confidence). It must never approve, reject, cancel or fulfil anything, and it authenticates with a service role that cannot.
7. **One JWT issuer.** `servicedesk-api` issues tokens; the other services validate them with the same signing key from the environment.
8. **The phone talks only to the reverse proxy.** `/api` routes to the C# API, `/notify` to the Spring service.

## How to work in this repo

- **One task per session.** Take the next unchecked task from `docs/PLAN.md`. Do not start a second task in the same session.
- **Plan before code.** State which files you will touch and how you will verify the result, then wait for a go-ahead on anything larger than a small fix.
- **Tests first** for domain rules, event handling and the copilot's tool calls.
- **Stay in your folder.** A task names one service. Do not edit another service's folder, `contracts/` or `docker-compose.yml` unless the task says so.
- **Small diffs.** If a change passes roughly 300 lines, split it and stop at the first half.
- **No new dependencies without asking.** Name the package, why it is needed and what the alternative is.
- **Stop after two failed attempts** at the same problem. Write down what you tried and ask.
- **Finish with a hand-off.** Copy `docs/handoffs/TEMPLATE.md` to `docs/handoffs/<task-id>.md` and fill it in. Ahmed reads every diff before merge, so the hand-off should tell him where to look hardest.
- **Log surprises.** When you find a bug you introduced, a wrong assumption or a rule you nearly broke, add two lines to `docs/build-log.md`.

## Extra rule for `servicedesk-api` (C#)

Ahmed comes from Java / Spring Boot and must be able to explain every line of the C# service in an interview. In the hand-off for any C# task, add a short "Java to C#" section: for each non-obvious construct you used, name the Spring or Java equivalent and the difference that matters. Prefer plain, idiomatic code over clever code.

## Things that must not happen

- **Demo data outside Development.** Seeding runs only when the environment is `Development`. This exact bug was caught in review once already.
- **Secrets in the repo.** Keys and passwords come from `.env`, which is git-ignored. Keep `.env.example` current.
- **Edited migrations.** Never change a migration that has been committed; add a new one.
- **Weakened or deleted tests** to make a change pass. If a test is wrong, say so and explain.
- **Overclaiming in docs.** Nothing here is in production. README and comments say "demo" or "deployed demo", never "production".
- **Scope creep.** Out of scope for this build: web frontend, user registration, Kubernetes, a second LLM provider, any fourth mobile screen.

## Definition of done

A task is done when its acceptance check in `docs/PLAN.md` passes, the service's tests pass, `docker compose up --build` still starts cleanly, the hand-off file exists, and the reviewer agent has reported no blocking findings.
