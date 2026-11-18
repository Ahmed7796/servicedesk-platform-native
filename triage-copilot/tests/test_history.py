import copy
import json
import re
from pathlib import Path
from typing import get_args

import httpx
import pytest
from pydantic import TypeAdapter

from app import models
from app.errors import ApiResponseError
from app.tools import TOOL_SPECS, TriageTools
from tests.test_tools import DETAIL, PATH as DETAIL_PATH, RID, SENTINEL, serve

pytestmark = pytest.mark.anyio

LIST = "/api/service-requests"
ME = "22222222-2222-4222-8222-222222222222"
OTHER = "99999999-9999-4999-8999-999999999999"
SECRET = "s3cret-value"


def item(n: int, requester: str = ME, **over) -> dict:
    base = {
        "id": f"00000000-0000-4000-8000-{n:012d}", "title": f"Req {n}", "description": "d", "type": "Leave",
        "status": "Approved", "requesterId": requester, "departmentId": "33333333-3333-4333-8333-333333333333",
        "createdAt": f"2026-01-{n:02d}T09:00:00Z", "updatedAt": f"2026-01-{n:02d}T09:00:00Z",
        "steps": [
            {"order": 1, "position": "HrManager", "scope": "Global", "decision": "Approved",
             "decidedById": None, "decidedAt": None, "comment": None, "escalatedFrom": None},
            {"order": 2, "position": "Newcomer", "scope": "Global", "decision": "Pending",
             "decidedById": None, "decidedAt": None, "comment": None, "escalatedFrom": None},
        ],
    }
    return {**base, **over}


def serve_list(stub, payload, status=200):
    stub.queue("GET", LIST, httpx.Response(status, json=payload))


@pytest.fixture
def tools(client):
    return TriageTools(client)


# --- get_requester_history -------------------------------------------------------------------

async def test_history_filters_orders_and_summarises(tools, stub):
    serve_list(stub, [item(3), item(9, requester=OTHER), item(5), item(1)])
    out = await tools.get_requester_history({"requester_id": ME})
    assert [r["id"][-2:] for r in out["requests"]] == ["05", "03", "01"]  # newest first
    assert (out["requester_id"], out["total_matching"], out["returned"]) == (ME, 3, 3)
    assert set(out["requests"][0]) == {"id", "title", "type", "status", "created_at", "steps_summary"}
    assert out["requests"][0]["steps_summary"] == "HrManager:Approved,Newcomer:Pending"
    assert [c.method for c in stub.calls if c.url.path != "/api/auth/service-token"] == ["GET"]
    assert stub.count("GET", LIST) == 1


@pytest.mark.parametrize("args,returned", [({}, 5), ({"limit": 2}, 2), ({"limit": 10}, 10)])
async def test_history_cap(tools, stub, args, returned):
    serve_list(stub, [item(n) for n in range(1, 13)])
    out = await tools.get_requester_history({"requester_id": ME, **args})
    assert out["returned"] == returned == len(out["requests"])
    assert out["total_matching"] == 12  # tells the model the cap truncated


async def test_history_exclude_and_empty(tools, stub):
    serve_list(stub, [item(1), item(2)])
    out = await tools.get_requester_history({"requester_id": ME, "exclude_request_id": item(2)["id"]})
    assert [r["title"] for r in out["requests"]] == ["Req 1"] and out["total_matching"] == 1
    serve_list(stub, [item(1, requester=OTHER)])
    empty = await tools.get_requester_history({"requester_id": ME})
    assert (empty["requests"], empty["total_matching"], empty["returned"]) == ([], 0, 0)


async def test_history_ids_compare_case_insensitively(tools, stub):
    upper_id = "AAAAAAAA-0000-4000-8000-000000000001"
    serve_list(stub, [item(1, requester=ME.upper(), id=upper_id), item(2, requester=ME.upper(), id=upper_id[:-1] + "2")])
    out = await tools.get_requester_history({"requester_id": ME, "exclude_request_id": upper_id.lower()})
    assert [r["id"] for r in out["requests"]] == [upper_id[:-1] + "2"]  # matched despite case, one excluded


async def test_history_same_created_at_orders_by_lowercased_id(tools, stub):
    first, second = "aaaaaaaa-0000-4000-8000-000000000001", "BBBBBBBB-0000-4000-8000-000000000001"
    same = "2026-02-01T09:00:00Z"
    serve_list(stub, [item(1, id=first, createdAt=same), item(2, id=second, createdAt=same)])
    out = await tools.get_requester_history({"requester_id": ME})
    assert [r["id"] for r in out["requests"]] == [second, first]  # reverse by lowered id, not raw ASCII


async def test_history_steps_summary_follows_step_order(tools, stub):
    reverse = list(reversed(item(1)["steps"]))
    serve_list(stub, [item(1, steps=reverse)])
    out = await tools.get_requester_history({"requester_id": ME})
    assert out["requests"][0]["steps_summary"] == "HrManager:Approved,Newcomer:Pending"


@pytest.mark.parametrize("args", [
    {}, {"requester_id": "nope"}, {"requester_id": ME, "exclude_request_id": "x"}, {"requester_id": ME, "x": 1},
    {"requester_id": ME, "limit": 0}, {"requester_id": ME, "limit": 11}, {"requester_id": ME, "limit": "5"},
    {"requester_id": ME, "limit": True}, {"requester_id": ME, "limit": 2.5},
])
async def test_history_bad_arguments_make_no_http_call(tools, stub, args):
    assert (await tools.get_requester_history(args))["error"] == "invalid_arguments"
    assert stub.calls == []


# --- list_requests (client) ------------------------------------------------------------------

async def test_list_requests_happy_path(client, stub):
    serve_list(stub, [item(1, surprise=1)])
    got = await client.list_requests()
    assert got[0].requester_id == ME and got[0].steps[1].position == "Newcomer"


async def test_list_requests_malformed_item_names_index_and_field(tools, stub):
    broken = item(2)
    del broken["title"]
    serve_list(stub, [item(1), broken, item(3, description=SENTINEL, status="Weird")])
    out = await tools.get_requester_history({"requester_id": ME})
    assert out["error"] == "bad_response"
    assert "1.title" in out["message"] and "2.status" in out["message"]
    assert SENTINEL not in out["message"]


@pytest.mark.parametrize("payload", [{"items": []}, "text", None])
async def test_list_requests_non_array_body(client, stub, payload):
    serve_list(stub, payload)
    with pytest.raises(ApiResponseError):
        await client.list_requests()


# --- dispatcher and specs --------------------------------------------------------------------

async def test_dispatch_routes_to_both_tools(tools, stub):
    serve(stub)
    serve_list(stub, [item(1)])
    assert (await tools.call("get_request", {"request_id": RID}))["found"] is True
    assert (await tools.call("get_requester_history", {"requester_id": ME}))["total_matching"] == 1


@pytest.mark.parametrize("name,args", [
    ("get_request", ["a"]), ("get_request", None), ("get_requester_history", "x"),
    ("delete_request", {}), ("approve", {"request_id": RID}),
    ("get_request", {"request_id": RID, "x": 1}), ("get_requester_history", {"requester_id": ME, "x": 1}),
])
async def test_dispatch_rejects_bad_calls(tools, stub, name, args):
    assert (await tools.call(name, args))["error"] == "invalid_arguments"
    assert stub.calls == []


def test_tool_specs_are_well_formed():
    assert [s["name"] for s in TOOL_SPECS] == ["get_request", "get_requester_history"]
    for spec in TOOL_SPECS:
        p = spec["parameters"]
        assert spec["description"] and p["type"] == "object" and p["additionalProperties"] is False
        assert set(p["required"]) <= set(p["properties"])
        json.dumps(spec)  # plain JSON, ready for any provider
    limit = TOOL_SPECS[1]["parameters"]["properties"]["limit"]
    assert (limit["type"], limit["minimum"], limit["maximum"], limit["default"]) == ("integer", 1, 10, 5)


async def test_unexpected_exception_is_raised_not_returned():
    class Boom:
        async def get_request(self, _):
            raise RuntimeError("bug")

        async def list_requests(self):
            raise RuntimeError("bug")

    t = TriageTools(Boom())
    with pytest.raises(RuntimeError):
        await t.call("get_request", {"request_id": RID})
    with pytest.raises(RuntimeError):
        await t.call("get_requester_history", {"requester_id": ME})


# --- no secret in any failure ----------------------------------------------------------------

@pytest.mark.parametrize("tool,args,path", [
    ("get_request", {"request_id": RID}, DETAIL_PATH),
    ("get_requester_history", {"requester_id": ME}, LIST),
])
@pytest.mark.parametrize("mode", ["401x2", "token401", 403, 503, "malformed"])
async def test_failures_never_leak_secret_or_token(tools, stub, tool, args, path, mode):
    if mode == "401x2":
        stub.queue("GET", path, httpx.Response(401), httpx.Response(401))
    elif mode == "token401":
        stub.token_status = 401
    elif mode == "malformed":
        stub.queue("GET", path, httpx.Response(200, json={"bad": SENTINEL}))
    else:
        stub.queue("GET", path, httpx.Response(mode))
    out = await tools.call(tool, args)
    assert "error" in out
    text = json.dumps(out)
    assert SECRET not in text and "tok-" not in text and SENTINEL not in text


# --- the models match the contract -----------------------------------------------------------

def _contracts() -> Path:
    root = Path(__file__).resolve().parents[2] / "contracts"
    if not root.is_dir():
        pytest.fail("contracts/ not found: run the tests with the whole repo available (see CLAUDE.md, Commands)")
    return root


def test_literals_match_contract_enums():
    root = _contracts()
    defs = json.loads((root / "events" / "defs.schema.json").read_text())["$defs"]
    yaml_text = (root / "openapi" / "servicedesk-api.yaml").read_text()
    m = re.search(r"TriagePriority:\s*\n\s*type: string\s*\n\s*enum: \[([^\]]+)\]", yaml_text)
    assert m, "TriagePriority enum not found in the OpenAPI file"
    pairs = {
        "requestType": models.RequestType, "requestStatus": models.RequestStatus,
        "stepDecision": models.StepDecision, "approvalScope": models.ApprovalScope,
        "auditAction": models.AuditAction,
    }
    expected = {name: defs[name]["enum"] for name in pairs}
    expected["priority"] = [v.strip() for v in m.group(1).split(",")]
    literals = {**pairs, "priority": models.TriagePriority}
    for name, lit in literals.items():
        adapter = TypeAdapter(lit)
        for value in expected[name]:
            assert adapter.validate_python(value) == value, f"{name}: {value}"
        assert set(get_args(lit)) == set(expected[name]), f"{name} has extra or missing values"
