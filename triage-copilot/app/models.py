"""Response models for the ServiceDesk API reads (contracts/openapi/servicedesk-api.yaml).

Unknown fields are ignored. Enums the contract closes are Literals, so drift fails loudly.
Approver positions are open labels (defs.schema.json) and stay plain strings.
"""
from datetime import datetime
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, field_validator
from pydantic.alias_generators import to_camel

RequestType = Literal["Equipment", "SoftwareAccess", "Leave", "Purchase"]
RequestStatus = Literal["Submitted", "InReview", "Approved", "Rejected", "Fulfilled", "Cancelled"]
StepDecision = Literal["Pending", "Approved", "Rejected", "Skipped", "Cancelled"]
ApprovalScope = Literal["RequesterDepartment", "Global"]
AuditAction = Literal["Submitted", "StepApproved", "StepRejected", "Fulfilled", "Cancelled", "StepEscalated"]
TriagePriority = Literal["Low", "Normal", "High", "Urgent"]


class ApiModel(BaseModel):
    model_config = ConfigDict(extra="ignore", alias_generator=to_camel, populate_by_name=True)


class ApprovalStep(ApiModel):
    order: int
    position: str
    scope: ApprovalScope
    decision: StepDecision
    comment: str | None = None
    escalated_from: str | None = None


class ServiceRequest(ApiModel):
    id: str
    title: str
    description: str
    type: RequestType
    status: RequestStatus
    requester_id: str
    department_id: str
    created_at: datetime
    updated_at: datetime
    steps: list[ApprovalStep]


class AuditEntry(ApiModel):
    action: AuditAction
    status_before: RequestStatus | None
    status_after: RequestStatus
    actor_name: str
    details: str | None = None
    occurred_at: datetime


class TriageSuggestion(ApiModel):
    category: RequestType
    priority: TriagePriority
    suggested_route: list[str]
    summary: str
    confidence: float


class ServiceRequestDetail(ServiceRequest):
    requester_name: str
    audit_trail: list[AuditEntry]
    triage: TriageSuggestion | None


Position = Annotated[str, StringConstraints(pattern=r"^[A-Z][A-Za-z0-9]*$", max_length=30)]


class TriageSuggestionInput(ApiModel):
    """What the copilot posts (TriageFields). Strict, like the API: no extra fields."""

    model_config = ConfigDict(extra="forbid")

    category: RequestType
    priority: TriagePriority
    suggested_route: list[Position] = Field(min_length=1, max_length=5)
    summary: str = Field(min_length=1, max_length=500)
    confidence: float = Field(strict=True, ge=0, le=1)  # no "0.5" strings, no true

    @field_validator("summary")
    @classmethod
    def _summary_not_blank(cls, v: str) -> str:
        if not v.strip():
            raise ValueError("summary must not be blank")
        return v
