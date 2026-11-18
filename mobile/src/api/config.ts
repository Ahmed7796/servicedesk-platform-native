import { client as apiClient } from './generated/servicedesk/client.gen';
import { client as notifyClient } from './generated/notification/client.gen';
import type { Client } from './generated/servicedesk/client';

type ResponseInterceptor = (response: Response, request: Request) => Response | Promise<Response>;

/**
 * Reverse-proxy base URL (architecture rule 8). There is deliberately no default:
 * a default would be the C# API directly, which bypasses the proxy.
 * Returns null when it is missing or blank, so the app can show a configuration error.
 */
export function getBaseUrl(raw: string | undefined = process.env.EXPO_PUBLIC_API_BASE_URL): string | null {
  const trimmed = raw?.trim().replace(/\/+$/, '');
  return trimmed ? trimmed : null;
}

/**
 * Points both generated clients at the proxy and adds the bearer token.
 * Contract paths already carry /api and /notify, so they sit under this one base URL.
 */
export function configureApi(
  baseUrl: string,
  getToken: () => Promise<string | null>,
  onUnauthorized?: (failedToken: string | null) => Promise<void>,
): void {
  const config = { baseUrl, auth: async () => (await getToken()) ?? undefined };
  apiClient.setConfig(config);
  notifyClient.setConfig(config);
  // Only the API ends the session (rule 7: it issues the token). A 401 from the notification
  // service is treated like any other failure of that best-effort call. If the token really
  // expired, the API calls get a 401 too.
  installUnauthorizedHandler(apiClient, onUnauthorized);
}

// The login call is the one 401 that is not a session problem: it means a wrong password.
function isLoginRequest(request: Request): boolean {
  return new URL(request.url).pathname.endsWith('/api/auth/login');
}

// The token this request was sent with, so a late 401 can be told apart from the current session.
function bearerToken(request: Request): string | null {
  const match = /^Bearer (.+)$/.exec(request.headers.get('Authorization') ?? '');
  return match ? match[1] : null;
}

// configureApi can run more than once (App renders), so the previous interceptor is ejected first.
const installed = new Map<Client, ResponseInterceptor>();

function installUnauthorizedHandler(
  client: Client,
  onUnauthorized?: (failedToken: string | null) => Promise<void>,
): void {
  const previous = installed.get(client);
  if (previous) client.interceptors.response.eject(previous);
  installed.delete(client);
  if (!onUnauthorized) return;
  const interceptor: ResponseInterceptor = async (response, request) => {
    if (response.status === 401 && !isLoginRequest(request)) await onUnauthorized(bearerToken(request));
    return response;
  };
  client.interceptors.response.use(interceptor);
  installed.set(client, interceptor);
}
