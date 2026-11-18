import copy

import httpx
import pytest

from app.servicedesk_client import ServiceDeskClient, ServiceTokenProvider
from app.tools import GET_REQUEST_SPEC, TriageTools
from tests.conftest import BASE

pytestmark = pytest.mark.anyio

RID = "11111111-1111-4111-8111-111111111111"
PATH = f"/api/service-requests/{RID}"
SENTINEL = "RAW-PAYLOAD-SENTINEL"

DETAIL = {
    "id": RID, "title": "New laptop", "description": f"Mine broke. {SENTINEL}", "type": "Equipment",
    "status": "InReview", "requesterId": "22222222-2222-4222-8222-222222222222",
    "departmentId": "33333333-3333-4333-8333-333333333333",
    "createdAt": "2026-01-02T09:00:00Z", "updatedAt": "2026-01-02T10:00:00Z",
    "steps": [
        {"order": 2, "position": "FinanceManager", "scope": "Global", "decision": "Pending",
         "decidedById": None, "decidedAt": None, "comment": None, "escalatedFrom": None},
        {"order": 1, "position": "DepartmentHead", "scope": "RequesterDepartment", "decision": "Approved",
         "decidedById": "44444444-4444-4444-8444-444444444444", "decidedAt": "2026-01-02T10:00:00Z",
         "comment": "ok", "escalatedFrom": None},
    ],
    "requesterName": "Sam Lee",
    "auditTrail": [
        {"action": "StepApproved", "statusBefore": "Submitted", "statusAfter": "InReview",
         "actorId": "44444444-4444-4444-8444-444444444444", "actorName": "Dana", "details": None,
         "occurredAt": "2026-01-02T10:00:00Z"},
        {"action": "Submitted", "statusBefore": None, "statusAfter": "Submitted",
         "actorId": "22222222-2222-4222-8222-222222222222", "actorName": "Sam Lee", "details": "via mobile",
         "occurredAt": "2026-01-02T09:00:00Z"},
    ],
    "triage": {"requestId": RID, "suggestedAt": "2026-01-02T09:01:00Z", "category": "Equipment",
               "priority": "High", "suggestedRoute": ["DepartmentHead", "NewPosition"],
               "summary": "Broken laptop", "confidence": 0.8},
}


@pytest.fixture
def tools(client):
    return TriageTools(client)


def serve(stub, payload=None, status=200):
    stub.queue("GET", PATH, httpx.Response(status, json=copy.deepcopy(payload if payload is not None else DETAIL)))


async def test_get_request_happy_path(tools, stub):
    serve(stub)
    out = await tools.get_request({"request_id": RID})
    assert set(out) == {"found", "id", "title", "description", "type", "status", "requester_id", "requester_name",
                        "department_id", "created_at", "steps", "audit_trail", "existing_triage"}
    assert out["description"] == DETAIL["description"]  # kept whole
    assert [s["order"] for s in out["steps"]] == [1, 2]
    assert set(out["steps"][0]) == {"order", "position", "scope", "decision", "comment", "escalated_from"}
    assert out["existing_triage"] == {"category": "Equipment", "priority": "High",
                                      "suggested_route": ["DepartmentHead", "NewPosition"],
                                      "summary": "Broken laptop", "confidence": 0.8}
    assert isinstance(out["existing_triage"]["confidence"], float)
    api_calls = [c for c in stub.calls if c.url.path == PATH]
    assert [c.method for c in stub.calls if c.url.path != "/api/auth/service-token"] == ["GET"]
    assert api_calls[0].headers["authorization"] == "Bearer tok-1"


async def test_triage_null(tools, stub):
    serve(stub, {**DETAIL, "triage": None})
    assert (await tools.get_request({"request_id": RID}))["existing_triage"] is None


async def test_audit_trail_returned_oldest_first(tools, stub):
    serve(stub)  # fixture lists the newer entry first
    out = await tools.get_request({"request_id": RID})
    assert [a["action"] for a in out["audit_trail"]] == ["Submitted", "StepApproved"]
    assert out["audit_trail"][0]["status_before"] is None


async def test_404_is_a_result_not_a_crash(tools, stub):
    stub.queue("GET", PATH, httpx.Response(404))
    out = await tools.get_request({"request_id": RID})
    assert out == {"found": False, "error": "request_not_found", "request_id": RID}


@pytest.mark.parametrize("args", [{"request_id": "../admin"}, {"request_id": "nope"}, {}, {"request_id": RID, "x": 1}])
async def test_bad_arguments_make_no_http_call(tools, stub, args):
    out = await tools.get_request(args)
    assert out["error"] == "invalid_arguments"
    assert stub.calls == []


async def test_unknown_fields_ignored_and_unknown_status_fails(tools, stub):
    extra = copy.deepcopy(DETAIL)
    extra["surprise"] = 1
    extra["steps"][0]["newField"] = "x"
    extra["auditTrail"][0]["newField"] = "x"
    extra["triage"]["newField"] = "x"
    serve(stub, extra)
    out = await tools.get_request({"request_id": RID})
    assert out["found"] and "surprise" not in out
    assert out["steps"][1]["position"] == "FinanceManager"
    assert "NewPosition" in out["existing_triage"]["suggested_route"]  # open string passes through

    serve(stub, {**DETAIL, "status": "Archived"})  # a closed enum with an unknown value
    bad = await tools.get_request({"request_id": RID})
    assert bad["error"] == "bad_response" and "status" in bad["message"]


async def test_malformed_payload_names_field_without_raw_payload(tools, stub):
    broken = {k: v for k, v in DETAIL.items() if k != "title"}
    broken["steps"] = "not a list"
    serve(stub, broken)
    out = await tools.get_request({"request_id": RID})
    assert out["error"] == "bad_response"
    assert "title" in out["message"] and "steps" in out["message"]
    assert SENTINEL not in out["message"] and "Mine broke" not in out["message"]

    stub.queue("GET", PATH, httpx.Response(200, content=b"<html>oops</html>"))
    assert (await tools.get_request({"request_id": RID}))["error"] == "bad_response"


@pytest.mark.parametrize("mode,code", [(403, "forbidden"), (503, "api_unavailable"), ("down", "api_unavailable")])
async def test_api_failures_become_tool_errors(stub, clock, mode, code):
    def handler(request: httpx.Request) -> httpx.Response:
        if mode == "down":
            raise httpx.ConnectError("refused", request=request)
        return stub._handle(request)

    if mode != "down":
        stub.queue("GET", PATH, httpx.Response(mode))
    async with httpx.AsyncClient(base_url=BASE, transport=httpx.MockTransport(handler)) as http:
        client = ServiceDeskClient(http, ServiceTokenProvider(http, "triage", "s3cret-value", clock=clock))
        out = await TriageTools(client).get_request({"request_id": RID})
    assert out["error"] == code
    assert "s3cret-value" not in str(out) and "tok-" not in str(out)


def test_spec_shape():
    assert GET_REQUEST_SPEC["name"] == "get_request"
    assert GET_REQUEST_SPEC["parameters"]["required"] == ["request_id"]
    assert GET_REQUEST_SPEC["parameters"]["additionalProperties"] is False
