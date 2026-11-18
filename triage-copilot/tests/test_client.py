import httpx
import pytest

from app.errors import ApiUnavailableError, ServiceAuthError
from tests.conftest import body

pytestmark = pytest.mark.anyio
TOKEN = "/api/auth/service-token"
PATH = "/api/service-requests/abc"


async def test_token_fetched_once_and_reused(client, stub):
    stub.queue("GET", PATH, *[httpx.Response(200, json={}) for _ in range(3)])
    for _ in range(3):
        await client._request("GET", PATH)
    assert stub.count("POST", TOKEN) == 1
    assert body(stub.calls[0]) == {"clientId": "triage", "clientSecret": "s3cret-value"}
    assert {c.headers["authorization"] for c in stub.calls if c.url.path == PATH} == {"Bearer tok-1"}


@pytest.mark.parametrize("fmt", ["z", "offset"])
async def test_token_refreshed_at_expiry_window(client, stub, clock, fmt):
    stub.expires_format = fmt  # also proves both date-time spellings parse
    stub.queue("GET", PATH, *[httpx.Response(200, json={}) for _ in range(3)])
    await client._request("GET", PATH)
    clock.advance(minutes=28)  # 2 minutes left, outside the 60 s skew: still cached
    await client._request("GET", PATH)
    assert stub.count("POST", TOKEN) == 1
    clock.advance(seconds=65)  # inside the skew window: refresh
    await client._request("GET", PATH)
    assert stub.count("POST", TOKEN) == 2
    assert stub.calls[-1].headers["authorization"] == "Bearer tok-2"


async def test_401_triggers_one_refresh_and_one_retry(client, stub):
    stub.queue("GET", PATH, httpx.Response(401), httpx.Response(200, json={"ok": 1}))
    resp = await client._request("GET", PATH)
    assert resp.status_code == 200
    assert stub.count("GET", PATH) == 2
    assert stub.count("POST", TOKEN) == 2
    assert stub.calls[-1].headers["authorization"] == "Bearer tok-2"


async def test_second_401_raises_without_looping(client, stub):
    stub.queue("GET", PATH, httpx.Response(401), httpx.Response(401), httpx.Response(200))
    with pytest.raises(ServiceAuthError):
        await client._request("GET", PATH)
    assert stub.count("GET", PATH) == 2
    assert stub.count("POST", TOKEN) == 2


async def test_bad_secret_raises_clear_error(client, stub):
    stub.token_status = 401
    with pytest.raises(ServiceAuthError) as err:
        await client._request("GET", PATH)
    assert "triage" in str(err.value)
    assert "s3cret-value" not in str(err.value)
    assert stub.count("POST", TOKEN) == 1
    assert stub.count("GET", PATH) == 0


async def test_403_is_returned_not_retried(client, stub):
    stub.queue("POST", "/api/service-requests/abc/approve", httpx.Response(403))
    resp = await client._request("POST", "/api/service-requests/abc/approve")
    assert resp.status_code == 403
    assert stub.count("POST", TOKEN) == 1
    assert stub.count("POST", "/api/service-requests/abc/approve") == 1


@pytest.mark.parametrize(
    "reply",
    [
        httpx.Response(200, content=b"not json"),
        httpx.Response(200, json={"expiresAt": "2026-01-01T12:30:00Z"}),
        httpx.Response(200, json={"accessToken": "t"}),
        httpx.Response(200, json={"accessToken": "t", "expiresAt": "garbage"}),
    ],
)
async def test_malformed_token_response_raises_api_unavailable(tokens, http, stub, reply):
    stub._handle = lambda request: reply
    http._transport = httpx.MockTransport(stub._handle)
    with pytest.raises(ApiUnavailableError) as err:
        await tokens.get_token()
    assert "malformed" in str(err.value)
    assert "s3cret-value" not in str(err.value)
