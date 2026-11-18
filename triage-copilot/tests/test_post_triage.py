import httpx
import pytest

from app.errors import ApiForbiddenError, ApiResponseError, ApiUnavailableError
from app.models import TriageSuggestionInput
from app.servicedesk_client import ServiceDeskClient
from tests.test_agent import GOOD, RID, STORED, TRIAGE
from tests.conftest import body

pytestmark = pytest.mark.anyio


async def test_post_triage_sends_five_fields_and_parses_reply(client, stub):
    stub.queue("POST", TRIAGE, httpx.Response(200, json={**STORED, "surprise": 1}))
    got = await client.post_triage(RID, TriageSuggestionInput.model_validate(GOOD))
    assert got.priority == "High" and got.confidence == 0.8
    sent = stub.calls[-1]
    assert (sent.method, sent.url.path, sent.headers["authorization"]) == ("POST", TRIAGE, "Bearer tok-1")
    assert body(sent) == GOOD


@pytest.mark.parametrize("status,exc", [(400, ApiResponseError), (403, ApiForbiddenError), (503, ApiUnavailableError)])
async def test_post_triage_error_mapping(client, stub, status, exc):
    stub.queue("POST", TRIAGE, httpx.Response(status))
    with pytest.raises(exc):
        await client.post_triage(RID, TriageSuggestionInput.model_validate(GOOD))


async def test_post_triage_404_is_none_and_it_is_the_only_write(client, stub):
    stub.queue("POST", TRIAGE, httpx.Response(404))
    assert await client.post_triage(RID, TriageSuggestionInput.model_validate(GOOD)) is None
    public = {n for n in dir(ServiceDeskClient) if not n.startswith("_")}
    assert public == {"get_request", "list_requests", "post_triage"}  # rule 6: no generic or other write
