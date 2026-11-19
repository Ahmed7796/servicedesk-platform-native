#!/usr/bin/env python3
"""End-to-end check of the demo flow (M5-2): submit, triage, approve twice, fulfil.

Runs against a fresh `docker compose up --build`, through the reverse proxy, as the phone would.
Standard library only. Uses the seeded demo accounts, which exist only in Development.

    python3 scripts/e2e.py [--base-url http://localhost:8000] [--triage-timeout 120]

Set COPILOT_MODE=canned in .env for a run that does not depend on Gemini being available.
Exit code 0 when every step passes, 1 on the first failure (the failing step is printed).
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

PASSWORD_ENV = "DEMO_PASSWORD"
DEFAULT_PASSWORD = "ServiceDesk!2026"  # the Development seed's demo password; never used outside Development
ACCOUNTS = {
    "requester": "requester@servicedesk.local",   # Operations
    "ops_head": "ops.head@servicedesk.local",     # DepartmentHead, Operations
    "it_manager": "it.manager@servicedesk.local", # ItManager, global
    "admin": "admin@servicedesk.local",           # fulfils
}


class StepFailed(Exception):
    pass


class Api:
    def __init__(self, base_url: str):
        self.base = base_url.rstrip("/")

    def call(self, method: str, path: str, body=None, token: str | None = None, expect: int = 200):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, method=method, data=data, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                status, raw, ctype = resp.status, resp.read(), resp.headers.get("Content-Type", "")
        except urllib.error.HTTPError as e:
            status, raw, ctype = e.code, e.read(), e.headers.get("Content-Type", "")
        except urllib.error.URLError as e:
            raise StepFailed(f"{method} {path}: cannot reach {self.base} ({e.reason})") from None
        if status != expect:
            raise StepFailed(f"{method} {path}: expected HTTP {expect}, got {status}")
        return json.loads(raw) if raw and "json" in ctype else (raw.decode(errors="replace") if raw else None)


def step(name: str):
    print(f"- {name} ... ", end="", flush=True)


def ok(detail: str = ""):
    print("ok" + (f" ({detail})" if detail else ""))


def run(base_url: str, triage_timeout: float) -> None:
    api = Api(base_url)
    password = os.environ.get(PASSWORD_ENV, DEFAULT_PASSWORD)

    step("proxy is up")
    api.call("GET", "/proxy-health")
    ok(base_url)

    step("sign in the four demo accounts through /api")
    tok = {k: api.call("POST", "/api/auth/login", {"identifier": v, "password": password})["accessToken"]
           for k, v in ACCOUNTS.items()}
    ok()

    step("requester submits an Equipment request")
    created = api.call("POST", "/api/service-requests", {
        "type": "Equipment",
        "title": "E2E check: replacement laptop",
        "description": "End-to-end check (M5-2). My laptop battery is broken and it shuts down within minutes.",
    }, tok["requester"], expect=201)
    rid = created["id"]
    ok(rid)

    step("copilot posts a triage suggestion")
    deadline = time.monotonic() + triage_timeout
    triage = None
    while time.monotonic() < deadline:
        detail = api.call("GET", f"/api/service-requests/{rid}", token=tok["requester"])
        triage = detail.get("triage")
        if triage:
            break
        time.sleep(3)
    if not triage:
        raise StepFailed(f"no triage within {triage_timeout:.0f}s (is the copilot healthy? "
                         "for a Gemini-independent run set COPILOT_MODE=canned)")
    if detail["status"] != "Submitted":
        raise StepFailed(f"triage changed the request status to {detail['status']} (rule 6)")
    ok(f"{triage['category']}/{triage['priority']}, route {triage['suggestedRoute']}")

    for who, position in (("ops_head", "DepartmentHead"), ("it_manager", "ItManager")):
        step(f"{who} sees it in pending-for-me and approves the {position} step")
        pending = api.call("GET", "/api/service-requests/pending-for-me", token=tok[who])
        if not any(p.get("requestId") == rid for p in pending):  # PendingItem.requestId
            raise StepFailed(f"request {rid} is not in {who}'s pending-for-me")
        api.call("POST", f"/api/service-requests/{rid}/approve", {"comment": "E2E approve"}, tok[who])
        ok()

    step("request is Approved")
    detail = api.call("GET", f"/api/service-requests/{rid}", token=tok["requester"])
    if detail["status"] != "Approved":
        raise StepFailed(f"expected status Approved, got {detail['status']}")
    ok()

    step("admin fulfils it")
    api.call("POST", f"/api/service-requests/{rid}/fulfil", token=tok["admin"])
    detail = api.call("GET", f"/api/service-requests/{rid}", token=tok["requester"])
    if detail["status"] != "Fulfilled":
        raise StepFailed(f"expected status Fulfilled, got {detail['status']}")
    ok()

    step("audit trail records the whole flow, oldest first")
    actions = [a["action"] for a in detail["auditTrail"]]
    # The API records each approval, including the last one, as StepApproved (no separate "Approved" entry).
    if actions[0] != "Submitted" or actions[-1] != "Fulfilled" or actions.count("StepApproved") != 2:
        raise StepFailed(f"unexpected audit trail {actions}")
    ok(" > ".join(actions))

    step("notify sees nothing overdue for it (step closed)")
    overdue = api.call("GET", "/notify/overdue", token=tok["admin"])
    if any(o.get("requestId") == rid for o in overdue):
        raise StepFailed("request still listed as overdue after it was decided")
    ok()


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--base-url", default=os.environ.get("E2E_BASE_URL", "http://localhost:8000"))
    p.add_argument("--triage-timeout", type=float, default=120.0)
    args = p.parse_args()
    print(f"E2E against {args.base_url}")
    try:
        run(args.base_url, args.triage_timeout)
    except StepFailed as e:
        print("FAILED")
        print(f"E2E failed: {e}")
        return 1
    print("E2E passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
