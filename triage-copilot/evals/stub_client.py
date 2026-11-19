"""In-process stand-in for ServiceDeskClient. Nothing here touches the network or a real API.

get_request returns the case as a ServiceRequestDetail with no label in it; history is empty;
post_triage records the suggestion instead of posting it.
"""
from uuid import NAMESPACE_URL, uuid5

from app.models import ServiceRequest, ServiceRequestDetail, TriageSuggestion, TriageSuggestionInput
from evals.cases import Case

ROUTES = {  # fixed synthetic approval chains, so the model has positions to reuse
    "Equipment": ["DepartmentHead", "ITManager"],
    "SoftwareAccess": ["DepartmentHead", "SecurityOfficer"],
    "Leave": ["DepartmentHead", "HRManager"],
    "Purchase": ["DepartmentHead", "FinanceManager"],
}
_NOW = "2026-01-05T09:00:00+00:00"


def request_id_for(case_id: str) -> str:
    return str(uuid5(NAMESPACE_URL, f"eval-request/{case_id}"))


def _detail(case: Case) -> ServiceRequestDetail:
    return ServiceRequestDetail.model_validate({
        "id": request_id_for(case.id), "title": case.title, "description": case.description,
        "type": case.submitted_type, "status": "Submitted",
        "requesterId": str(uuid5(NAMESPACE_URL, f"eval-requester/{case.id}")), "requesterName": "Eval Requester",
        "departmentId": str(uuid5(NAMESPACE_URL, "eval-department")),
        "createdAt": _NOW, "updatedAt": _NOW,
        "steps": [{"order": i, "position": p, "scope": "RequesterDepartment" if i == 1 else "Global",
                   "decision": "Pending"} for i, p in enumerate(ROUTES[case.submitted_type], 1)],
        "auditTrail": [{"action": "Submitted", "statusBefore": None, "statusAfter": "Submitted",
                        "actorName": "Eval Requester", "details": None, "occurredAt": _NOW}],
        "triage": None,
    })


class StubServiceDesk:
    def __init__(self, cases: list[Case]) -> None:
        self._details = {request_id_for(c.id): _detail(c) for c in cases}
        self.captured: dict[str, TriageSuggestionInput] = {}

    async def get_request(self, request_id: str) -> ServiceRequestDetail | None:
        return self._details.get(request_id)

    async def list_requests(self) -> list[ServiceRequest]:
        return []

    async def post_triage(self, request_id: str, suggestion: TriageSuggestionInput) -> TriageSuggestion | None:
        if request_id not in self._details:
            return None
        self.captured[request_id] = suggestion
        return TriageSuggestion(category=suggestion.category, priority=suggestion.priority,
                                suggested_route=suggestion.suggested_route, summary=suggestion.summary,
                                confidence=suggestion.confidence)
