# triage-copilot (demo)

FastAPI service that will suggest triage for new service requests. Advisory only: it never approves,
rejects, cancels or fulfils. This skeleton has `/health` and a service-token client for the C# API.

## Environment

| Variable | Meaning |
|---|---|
| `SERVICEDESK_API_URL` | Base URL of the C# API (Compose: `http://api:8080`) |
| `TRIAGE_CLIENT_ID` | Service client ID |
| `TRIAGE_CLIENT_SECRET` | Service client secret (from `.env`, never the repo) |
| `TOKEN_REFRESH_SKEW_SECONDS` | Optional, default 60: refresh this long before expiry |
| `HTTP_TIMEOUT_SECONDS` | Optional, default 10 |

## Test (Python 3.12, in Docker)

From the repo root:

    docker run --rm -v "$PWD":/repo -w /repo/triage-copilot python:3.12-slim sh -c "pip install -q -r requirements-dev.txt && pytest -q"

## Run

    uvicorn app.main:app --port 8082 --reload
