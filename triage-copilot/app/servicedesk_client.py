from collections.abc import Callable
from datetime import datetime, timedelta, timezone

import httpx
from pydantic import SecretStr

from app.errors import ApiUnavailableError, ServiceAuthError

TOKEN_PATH = "/api/auth/service-token"


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


class ServiceTokenProvider:
    """Fetches and caches the TriageService token (about 30 minutes)."""

    def __init__(
        self,
        http: httpx.AsyncClient,
        client_id: str,
        client_secret: SecretStr | str,
        skew_seconds: int = 60,
        clock: Callable[[], datetime] = _utcnow,
    ) -> None:
        self._http = http
        self._client_id = client_id
        self._client_secret = SecretStr(client_secret) if isinstance(client_secret, str) else client_secret
        self._skew = timedelta(seconds=skew_seconds)
        self._clock = clock
        self._token: str | None = None
        self._expires_at: datetime | None = None

    async def get_token(self) -> str:
        if self._token and self._expires_at and self._clock() < self._expires_at - self._skew:
            return self._token
        return await self._fetch()

    def invalidate(self) -> None:
        self._token = None
        self._expires_at = None

    async def _fetch(self) -> str:
        try:
            resp = await self._http.post(
                TOKEN_PATH,
                json={"clientId": self._client_id, "clientSecret": self._client_secret.get_secret_value()},
            )
        except httpx.HTTPError as exc:
            raise ApiUnavailableError(f"token endpoint unreachable: {exc}") from exc
        if resp.status_code == 401:
            raise ServiceAuthError(
                f"ServiceDesk API rejected service credentials for client '{self._client_id}' (401)"
            )
        if resp.status_code != 200:
            raise ApiUnavailableError(f"token endpoint returned {resp.status_code}")
        try:
            body = resp.json()
            token = body["accessToken"]
            expires = datetime.fromisoformat(body["expiresAt"])  # accepts "Z" and "+00:00"
            if not isinstance(token, str) or not token:
                raise ValueError("empty accessToken")
        except (ValueError, KeyError, TypeError) as exc:
            raise ApiUnavailableError(
                f"token endpoint returned a malformed 200 response ({type(exc).__name__})"
            ) from None
        if expires.tzinfo is None:
            expires = expires.replace(tzinfo=timezone.utc)
        self._token = token
        self._expires_at = expires
        return self._token


class ServiceDeskClient:
    """Calls the ServiceDesk API with a Bearer token. On a 401 it refreshes once and retries once."""

    def __init__(self, http: httpx.AsyncClient, tokens: ServiceTokenProvider) -> None:
        self._http = http
        self._tokens = tokens

    async def _request(self, method: str, path: str, **kwargs) -> httpx.Response:
        resp = await self._send(method, path, **kwargs)
        if resp.status_code == 401:
            self._tokens.invalidate()
            resp = await self._send(method, path, **kwargs)
            if resp.status_code == 401:
                raise ServiceAuthError("ServiceDesk API returned 401 again after a token refresh")
        return resp  # 403 and others are returned as they are; the caller decides.

    async def _send(self, method: str, path: str, **kwargs) -> httpx.Response:
        token = await self._tokens.get_token()
        try:
            return await self._http.request(
                method, path, headers={"Authorization": f"Bearer {token}"}, **kwargs
            )
        except httpx.HTTPError as exc:
            raise ApiUnavailableError(f"{method} {path} failed: {exc}") from exc


def build_client(base_url: str, client_id: str, client_secret: SecretStr | str, skew: int, timeout: float):
    """Wire the real httpx client, token provider and API client. Caller closes the returned http."""
    http = httpx.AsyncClient(base_url=base_url, timeout=timeout)
    return http, ServiceDeskClient(http, ServiceTokenProvider(http, client_id, client_secret, skew))
