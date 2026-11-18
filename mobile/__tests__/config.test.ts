import { configureApi, getBaseUrl } from '../src/api/config';
import { login, listPendingForMe } from '../src/api/generated/servicedesk';
import { getHealth } from '../src/api/generated/notification';
import { client as apiClient } from '../src/api/generated/servicedesk/client.gen';
import { client as notifyClient } from '../src/api/generated/notification/client.gen';

describe('getBaseUrl', () => {
  const original = process.env.EXPO_PUBLIC_API_BASE_URL;
  afterEach(() => {
    if (original === undefined) delete process.env.EXPO_PUBLIC_API_BASE_URL;
    else process.env.EXPO_PUBLIC_API_BASE_URL = original;
  });

  it('is null when missing or blank (no default)', () => {
    delete process.env.EXPO_PUBLIC_API_BASE_URL;
    expect(getBaseUrl(undefined)).toBeNull();
    expect(getBaseUrl('  ')).toBeNull();
  });
  it('strips trailing slashes', () => {
    expect(getBaseUrl('http://proxy:8000//')).toBe('http://proxy:8000');
  });
});

describe('generated clients', () => {
  it('have no baseUrl until configureApi runs (guards against a generated default)', () => {
    expect(apiClient.getConfig().baseUrl).toBeUndefined();
    expect(notifyClient.getConfig().baseUrl).toBeUndefined();
  });
});

describe('configureApi', () => {
  const fetchMock = jest.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    jest.spyOn(globalThis, 'fetch').mockImplementation(fetchMock);
  });

  it('keeps /api and /notify prefixes under the one base URL', async () => {
    configureApi('http://proxy:8000', async () => null);
    await login({ body: { identifier: 'a', password: 'b' } });
    await getHealth();
    const urls = fetchMock.mock.calls.map(([req]) => (req as Request).url);
    expect(urls).toEqual(['http://proxy:8000/api/auth/login', 'http://proxy:8000/notify/health']);
  });

  it('sends the stored token as a bearer header, and none when absent', async () => {
    configureApi('http://proxy:8000', async () => 'tok');
    await listPendingForMe();
    configureApi('http://proxy:8000', async () => null);
    await listPendingForMe();
    const auth = fetchMock.mock.calls.map(([req]) => (req as Request).headers.get('Authorization'));
    expect(auth).toEqual(['Bearer tok', null]);
  });
});
