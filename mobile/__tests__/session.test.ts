const mockStore = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(async (k: string) => mockStore.get(k) ?? null),
  setItemAsync: jest.fn(async (k: string, v: string) => void mockStore.set(k, v)),
  deleteItemAsync: jest.fn(async (k: string) => void mockStore.delete(k)),
}));
import { configureApi } from '../src/api/config';
import { login, listPendingForMe, listServiceRequests } from '../src/api/generated/servicedesk';
import { getHealth } from '../src/api/generated/notification';
import { handleUnauthorized, navigationRef } from '../src/auth/session';
import { client as apiClient } from '../src/api/generated/servicedesk/client.gen';
import { client as notifyClient } from '../src/api/generated/notification/client.gen';
import { tokenStore } from '../src/auth/tokenStore';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
let reset: jest.SpyInstance;

const respond = (status: number) => () =>
  Promise.resolve(
    new Response(JSON.stringify({ title: 't', status }), { status, headers: { 'Content-Type': 'application/problem+json' } }),
  );

beforeEach(async () => {
  mockStore.clear();
  fetchMock.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  jest.spyOn(navigationRef, 'isReady').mockReturnValue(true);
  reset = jest.spyOn(navigationRef, 'reset').mockImplementation(() => undefined);
  configureApi('http://proxy.test', tokenStore.get, handleUnauthorized);
  await tokenStore.set('old-token');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
  jest.restoreAllMocks();
});

describe('401 session handling', () => {
  it('a 401 from a non-login call clears the token and resets to Login', async () => {
    fetchMock.mockImplementation(respond(401));
    await listPendingForMe();
    expect(await tokenStore.get()).toBeNull();
    expect(reset).toHaveBeenCalledTimes(1);
    expect(reset).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'Login' }] });
  });

  it('a 401 from the login call (wrong password) does not touch the token or navigation', async () => {
    fetchMock.mockImplementation(respond(401));
    await login({ body: { identifier: 'a@b.c', password: 'bad' } });
    expect(await tokenStore.get()).toBe('old-token');
    expect(reset).not.toHaveBeenCalled();
  });

  it('other statuses (403, 409) do not end the session', async () => {
    fetchMock.mockImplementation(respond(403));
    await listPendingForMe();
    fetchMock.mockImplementation(respond(409));
    await listPendingForMe();
    expect(await tokenStore.get()).toBe('old-token');
    expect(reset).not.toHaveBeenCalled();
  });

  it('parallel 401s, across both clients, reset once', async () => {
    fetchMock.mockImplementation(respond(401));
    await Promise.all([listPendingForMe(), listPendingForMe(), getHealth()]);
    expect(reset).toHaveBeenCalledTimes(1);
    // A late 401 after sign-out finds no token and does nothing more.
    await listPendingForMe();
    expect(reset).toHaveBeenCalledTimes(1);
  });

  it('a stale 401 from the old token does not end a new session', async () => {
    let finishSlow!: (r: Response) => void;
    // The pending-for-me call is sent with old-token and answers late, after the user has signed in again.
    fetchMock.mockImplementation((request: Request) =>
      request.url.endsWith('/pending-for-me') ? new Promise<Response>((r) => (finishSlow = r)) : respond(401)(),
    );
    const slow = listPendingForMe();
    await Promise.all([listServiceRequests(), listServiceRequests(), getHealth()]);
    expect(reset).toHaveBeenCalledTimes(1);

    await tokenStore.set('new-token'); // signed in again
    finishSlow(await respond(401)());
    await slow;

    expect(await tokenStore.get()).toBe('new-token');
    expect(reset).toHaveBeenCalledTimes(1);
  });

  it('configureApi called again replaces the handler instead of stacking another', () => {
    configureApi('http://proxy.test', tokenStore.get, handleUnauthorized);
    configureApi('http://proxy.test', tokenStore.get, handleUnauthorized);
    for (const c of [apiClient, notifyClient]) {
      expect(c.interceptors.response.fns.filter(Boolean)).toHaveLength(1);
    }
  });
});
