import { client as apiClient } from './generated/servicedesk/client.gen';
import { client as notifyClient } from './generated/notification/client.gen';

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
export function configureApi(baseUrl: string, getToken: () => Promise<string | null>): void {
  const config = { baseUrl, auth: async () => (await getToken()) ?? undefined };
  apiClient.setConfig(config);
  notifyClient.setConfig(config);
}
