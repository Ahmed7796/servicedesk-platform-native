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

HISTORY_DEFAULT_LIMIT = 5
HISTORY_MAX_LIMIT = 10

GET_REQUESTER_HISTORY_SPEC = {
    "name": "get_requester_history",
    "description": "List a requester's recent requests, newest first, to spot repeats and patterns.",
    "parameters": {
        "type": "object",
        "properties": {
            "requester_id": {"type": "string", "format": "uuid", "description": "Id of the requester."},
            "exclude_request_id": {"type": "string", "format": "uuid",
                                   "description": "Request to leave out, usually the one being triaged."},
            "limit": {"type": "integer", "minimum": 1, "maximum": HISTORY_MAX_LIMIT,
                      "default": HISTORY_DEFAULT_LIMIT, "description": "How many requests to return."},
        },
        "required": ["requester_id"],
        "additionalProperties": False,
    },
}

TOOL_SPECS = [GET_REQUEST_SPEC, GET_REQUESTER_HISTORY_SPEC]


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


def _unknown_args(args: dict, allowed: set[str]) -> dict | None:
    extra = set(args) - allowed
    if extra:
        return tool_error("invalid_arguments", f"unknown argument(s): {', '.join(sorted(extra))}")
    return None


def _uuid_arg(args: dict, name: str) -> str | dict:
    try:
        return str(UUID(str(args[name])))
    except (KeyError, ValueError):
        return tool_error("invalid_arguments", f"'{name}' must be a UUID")


class TriageTools:
    def __init__(self, client: ServiceDeskClient) -> None:
        self._client = client

    async def call(self, name: str, args: object) -> dict:
        """Run a tool by name. Bad names or arguments come back as invalid_arguments results."""
        handlers = {"get_request": self.get_request, "get_requester_history": self.get_requester_history}
        if name not in handlers:
            return tool_error("invalid_arguments", f"unknown tool '{name}'")
        if not isinstance(args, dict):
            return tool_error("invalid_arguments", "arguments must be a JSON object")
        return await handlers[name](args)

    async def get_requester_history(self, args: dict) -> dict:
        bad = _unknown_args(args, {"requester_id", "exclude_request_id", "limit"})
        if bad:
            return bad
        requester_id = _uuid_arg(args, "requester_id")
        if isinstance(requester_id, dict):
            return requester_id
        exclude = None
        if "exclude_request_id" in args:
            exclude = _uuid_arg(args, "exclude_request_id")
            if isinstance(exclude, dict):
                return exclude
        limit = args.get("limit", HISTORY_DEFAULT_LIMIT)
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= HISTORY_MAX_LIMIT:
            return tool_error("invalid_arguments", f"'limit' must be an integer from 1 to {HISTORY_MAX_LIMIT}")
        try:
            # The contract has no server-side filter: fetch everything, filter here.
            everything = await self._client.list_requests()
        except Exception as exc:
            mapped = error_from_exception(exc)
            if mapped is None:
                raise
            return mapped
        mine = [r for r in everything if r.requester_id.lower() == requester_id and r.id.lower() != exclude]
        mine.sort(key=lambda r: (r.created_at, r.id.lower()), reverse=True)
        return {
            "requester_id": requester_id,
            "total_matching": len(mine),
            "returned": min(len(mine), limit),
            "requests": [
                {"id": r.id, "title": r.title, "type": r.type, "status": r.status,
                 "created_at": r.created_at.isoformat(),
                 "steps_summary": ",".join(f"{s.position}:{s.decision}" for s in sorted(r.steps, key=lambda s: s.order))}
                for r in mine[:limit]
            ],
        }

    async def get_request(self, args: dict) -> dict:
        bad = _unknown_args(args, {"request_id"})
        if bad:
            return bad
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
