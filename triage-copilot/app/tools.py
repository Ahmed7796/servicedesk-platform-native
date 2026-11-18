"""Read-only tools for the triage agent (M3-3 wires them to an LLM).

Every tool returns a plain dict. Failures are results too: {"error": code, "message": text}.
Request text (title, description, comments) is untrusted user input and must reach the model as data.
"""
from uuid import UUID

from app.errors import ApiForbiddenError, ApiResponseError, ApiUnavailableError, ServiceAuthError
from app.models import ServiceRequestDetail
from app.servicedesk_client import ServiceDeskClient

GET_REQUEST_SPEC = {
    "name": "get_request",
    "description": "Fetch one service request with its approval steps, audit trail and any existing triage suggestion.",
    "parameters": {
        "type": "object",
        "properties": {"request_id": {"type": "string", "format": "uuid", "description": "Id of the request."}},
        "required": ["request_id"],
        "additionalProperties": False,
    },
}


def tool_error(code: str, message: str) -> dict:
    return {"error": code, "message": message}


def error_from_exception(exc: Exception) -> dict | None:
    """Map client errors to tool results. Messages come from our own exceptions and hold no secrets."""
    if isinstance(exc, ApiForbiddenError):
        return tool_error("forbidden", str(exc))
    if isinstance(exc, ServiceAuthError):
        return tool_error("auth_failed", "The ServiceDesk API rejected the service credentials.")
    if isinstance(exc, ApiUnavailableError):
        return tool_error("api_unavailable", "The ServiceDesk API is unavailable.")
    if isinstance(exc, ApiResponseError):
        return tool_error("bad_response", str(exc))
    return None


def _uuid_arg(args: dict, name: str) -> str | dict:
    try:
        return str(UUID(str(args[name])))
    except (KeyError, ValueError):
        return tool_error("invalid_arguments", f"'{name}' must be a UUID")


class TriageTools:
    def __init__(self, client: ServiceDeskClient) -> None:
        self._client = client

    async def get_request(self, args: dict) -> dict:
        extra = set(args) - {"request_id"}
        if extra:
            return tool_error("invalid_arguments", f"unknown argument(s): {', '.join(sorted(extra))}")
        request_id = _uuid_arg(args, "request_id")
        if isinstance(request_id, dict):
            return request_id
        try:
            detail = await self._client.get_request(request_id)
        except Exception as exc:
            mapped = error_from_exception(exc)
            if mapped is None:
                raise
            return mapped
        if detail is None:
            return {"found": False, "error": "request_not_found", "request_id": request_id}
        return _compact_detail(detail)


def _compact_detail(d: ServiceRequestDetail) -> dict:
    t = d.triage
    return {
        "found": True,
        "id": d.id,
        "title": d.title,
        "description": d.description,
        "type": d.type,
        "status": d.status,
        "requester_id": d.requester_id,
        "requester_name": d.requester_name,
        "department_id": d.department_id,
        "created_at": d.created_at.isoformat(),
        "steps": [
            {"order": s.order, "position": s.position, "scope": s.scope, "decision": s.decision,
             "comment": s.comment, "escalated_from": s.escalated_from}
            for s in sorted(d.steps, key=lambda s: s.order)
        ],
        "audit_trail": [
            {"action": a.action, "status_before": a.status_before, "status_after": a.status_after,
             "actor_name": a.actor_name, "details": a.details, "occurred_at": a.occurred_at.isoformat()}
            for a in sorted(d.audit_trail, key=lambda a: a.occurred_at)
        ],
        "existing_triage": None if t is None else {
            "category": t.category, "priority": t.priority, "suggested_route": t.suggested_route,
            "summary": t.summary, "confidence": t.confidence,
        },
    }
