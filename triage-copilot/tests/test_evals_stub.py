import pytest

from app.models import ServiceRequestDetail, TriageSuggestionInput
from app.tools import TriageTools
from evals.cases import Case
from evals.stub_client import StubServiceDesk, request_id_for

pytestmark = pytest.mark.anyio


def _case(i=1):
    # the requester picked Equipment; the checked labels are different strings that must never reach the model
    return Case(id=f"c{i}", kind="mislabelled", title="Broken dock", description="Dock drops displays.",
                submitted_type="Equipment", category="SoftwareAccess", priority="Urgent", note="LABEL-NOTE")


async def test_get_request_returns_the_case_as_detail_without_labels():
    stub = StubServiceDesk([_case()])
    detail = await stub.get_request(request_id_for("c1"))
    assert isinstance(detail, ServiceRequestDetail)
    assert (detail.title, detail.description, detail.type) == ("Broken dock", "Dock drops displays.", "Equipment")
    assert detail.status == "Submitted" and detail.triage is None and len(detail.steps) >= 1
    assert len(detail.audit_trail) == 1


async def test_detail_text_has_no_label_fields():
    detail = await StubServiceDesk([_case()]).get_request(request_id_for("c1"))
    dumped = detail.model_dump()
    assert "priority" not in dumped and "note" not in dumped and "kind" not in dumped
    blob = detail.model_dump_json()
    for label in ("SoftwareAccess", "Urgent", "LABEL-NOTE", "mislabelled"):
        assert label not in blob


async def test_unknown_id_is_none_and_history_is_empty():
    stub = StubServiceDesk([_case()])
    assert await stub.get_request("11111111-1111-1111-1111-111111111111") is None
    assert await stub.list_requests() == []


async def test_post_triage_captures_and_returns_a_suggestion():
    stub = StubServiceDesk([_case()])
    s = TriageSuggestionInput(category="Equipment", priority="High", suggestedRoute=["DepartmentHead"],
                              summary="x", confidence=0.9)
    stored = await stub.post_triage(request_id_for("c1"), s)
    assert stored is not None and stored.category == "Equipment"
    assert stub.captured[request_id_for("c1")] == s


async def test_works_through_the_real_tools():
    stub = StubServiceDesk([_case()])
    result = await TriageTools(stub).call("get_request", {"request_id": request_id_for("c1")})
    assert result["found"] is True and result["title"] == "Broken dock"
    hist = await TriageTools(stub).call("get_requester_history",
                                        {"requester_id": result["requester_id"], "limit": 3})
    assert hist["total_matching"] == 0


def test_ids_are_stable_uuids():
    assert request_id_for("c1") == request_id_for("c1") != request_id_for("c2")
    assert len(request_id_for("c1")) == 36
