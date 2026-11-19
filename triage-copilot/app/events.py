"""Event envelope check (contracts/events/envelope.schema.json), lenient the way consumers must be.

Unknown fields are ignored at every level (contract rule 2). The payload is not inspected: the copilot reads the
request through the API. `position` and the rest of the payload therefore stay opaque (rule 4).
"""
import json
import re
from datetime import datetime
from uuid import UUID

from pydantic import BaseModel, ConfigDict, StrictInt, StrictStr, ValidationError, field_validator


class EnvelopeError(Exception):
    """The body is not a valid envelope. The message is fixed text: it never repeats the body."""


class Envelope(BaseModel):
    model_config = ConfigDict(extra="ignore")

    event_id: StrictStr
    type: StrictStr
    occurred_at: datetime
    request_id: StrictStr
    actor_id: StrictStr
    payload: dict
    version: StrictInt

    @field_validator("event_id", "request_id", "actor_id")
    @classmethod
    def _uuid(cls, v: str) -> str:
        return str(UUID(v))

    @field_validator("type")
    @classmethod
    def _type(cls, v: str) -> str:
        if not re.fullmatch(r"request\.[a-z_]+", v):
            raise ValueError("type")
        return v

    @field_validator("version")
    @classmethod
    def _version(cls, v: int) -> int:
        if isinstance(v, bool) or v < 1:
            raise ValueError("version")
        return v


_ALIASES = {"eventId": "event_id", "occurredAt": "occurred_at", "requestId": "request_id", "actorId": "actor_id"}


def parse_envelope(raw: bytes) -> Envelope:
    try:
        data = json.loads(raw)
    except (ValueError, UnicodeDecodeError):
        raise EnvelopeError("body is not JSON") from None
    if not isinstance(data, dict):
        raise EnvelopeError("body is not a JSON object")
    data = {_ALIASES.get(k, k): v for k, v in data.items()}
    try:
        return Envelope.model_validate(data)
    except ValidationError:
        raise EnvelopeError("body is not a valid envelope") from None
