import json
from pathlib import Path

import pytest

from app.events import EnvelopeError, parse_envelope

EXAMPLE = Path(__file__).resolve().parents[2] / "contracts/events/examples/request.submitted.json"


def example() -> dict:
    return json.loads(EXAMPLE.read_text())


def enc(d) -> bytes:
    return json.dumps(d).encode()


def test_contract_example_parses():
    env = parse_envelope(EXAMPLE.read_bytes())
    assert env.type == "request.submitted" and env.version == 1
    assert env.request_id == "3f6c2a9e-8b1d-4e57-9a2c-1d7e5b4f0a11"


def test_unknown_fields_are_ignored_at_any_level():
    d = example()
    d["surprise"] = 1
    d["payload"]["steps"][0]["position"] = "BrandNewPosition"  # rule 4: opaque
    d["payload"]["nested"] = {"x": [1]}
    assert parse_envelope(enc(d)).event_id == d["eventId"]


@pytest.mark.parametrize("raw", [b"", b"not json", b"[]", b'"x"', b"null", b"\xff\xfe"])
def test_not_a_json_object(raw):
    with pytest.raises(EnvelopeError):
        parse_envelope(raw)


@pytest.mark.parametrize("field", ["eventId", "type", "occurredAt", "requestId", "actorId", "payload", "version"])
def test_missing_field(field):
    d = example()
    del d[field]
    with pytest.raises(EnvelopeError):
        parse_envelope(enc(d))


@pytest.mark.parametrize("field,value", [
    ("eventId", "nope"), ("requestId", "123"), ("actorId", None), ("occurredAt", "yesterday"),
    ("version", 0), ("version", "1"), ("version", True), ("payload", []), ("type", 5), ("type", "Request.Submitted"),
])
def test_bad_value(field, value):
    d = example()
    d[field] = value
    with pytest.raises(EnvelopeError):
        parse_envelope(enc(d))


def test_error_never_carries_input_text():
    d = example()
    d["eventId"] = "SENTINEL-TEXT"
    with pytest.raises(EnvelopeError) as err:
        parse_envelope(enc(d))
    assert "SENTINEL" not in str(err.value) and "SENTINEL" not in repr(err.value)
