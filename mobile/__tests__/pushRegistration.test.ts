import { Platform } from 'react-native';
import { Alert } from 'react-native';
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn().mockResolvedValue('jwt'),
  deleteItemAsync: jest.fn().mockResolvedValue(undefined),
}));
import * as SecureStore from 'expo-secure-store';
import { configureApi } from '../src/api/config';
import { registerForPush } from '../src/push/registerForPush';
import { getProjectId } from '../src/push/projectId';

const originalFetch = globalThis.fetch;
const originalOS = Platform.OS;
const fetchMock = jest.fn();
const onUnauthorized = jest.fn().mockResolvedValue(undefined);
const constants = Constants as unknown as { expoConfig: unknown; easConfig: unknown };
const n = jest.mocked(Notifications);
const PROJECT = '44444444-4444-4444-8444-444444444444';
const TOKEN = 'ExponentPushToken[abc123]';

const grant = () => {
  n.getPermissionsAsync.mockResolvedValue({ status: 'granted' } as never);
  n.getExpoPushTokenAsync.mockResolvedValue({ data: TOKEN, type: 'expo' } as never);
};
const deviceCalls = () => fetchMock.mock.calls.filter((c) => (c[0] as Request).url.endsWith('/notify/devices'));

beforeEach(() => {
  fetchMock.mockReset().mockResolvedValue(new Response(null, { status: 204 }));
  onUnauthorized.mockClear();
  n.setNotificationChannelAsync.mockClear();
  n.requestPermissionsAsync.mockReset().mockResolvedValue({ status: 'denied' } as never);
  n.getPermissionsAsync.mockReset().mockResolvedValue({ status: 'denied' } as never);
  n.getExpoPushTokenAsync.mockReset();
  (SecureStore.deleteItemAsync as jest.Mock).mockClear();
  constants.expoConfig = { extra: { eas: { projectId: PROJECT } } };
  constants.easConfig = undefined;
  Platform.OS = 'android';
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt', onUnauthorized);
});
afterEach(() => {
  jest.restoreAllMocks();
  globalThis.fetch = originalFetch;
  Platform.OS = originalOS;
});

describe('getProjectId', () => {
  it('reads expoConfig.extra.eas.projectId first, then easConfig', () => {
    expect(getProjectId()).toBe(PROJECT);
    constants.expoConfig = {};
    constants.easConfig = { projectId: 'from-eas' };
    expect(getProjectId()).toBe('from-eas');
  });
  it('is null when neither is set or the value is blank', () => {
    constants.expoConfig = {};
    expect(getProjectId()).toBeNull();
    constants.expoConfig = { extra: { eas: { projectId: '  ' } } };
    expect(getProjectId()).toBeNull();
  });
});

describe('registerForPush', () => {
  it('without a projectId does nothing: no permission prompt, no call, no alert, no crash', async () => {
    grant();
    constants.expoConfig = {};
    const alert = jest.spyOn(Alert, 'alert');
    await expect(registerForPush()).resolves.toBeUndefined();
    expect(n.requestPermissionsAsync).not.toHaveBeenCalled();
    expect(n.getExpoPushTokenAsync).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(alert).not.toHaveBeenCalled();
  });

  it('with permission denied (after asking) makes no token request and no call', async () => {
    await registerForPush();
    expect(n.requestPermissionsAsync).toHaveBeenCalledTimes(1);
    expect(n.getExpoPushTokenAsync).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('asks for permission only when it is not already granted', async () => {
    grant();
    await registerForPush();
    expect(n.requestPermissionsAsync).not.toHaveBeenCalled();
    n.getPermissionsAsync.mockResolvedValue({ status: 'undetermined' } as never);
    n.requestPermissionsAsync.mockResolvedValue({ status: 'granted' } as never);
    await registerForPush();
    expect(n.requestPermissionsAsync).toHaveBeenCalledTimes(1);
    expect(deviceCalls()).toHaveLength(2);
  });

  it('when granted posts the token and platform to /notify/devices with the bearer header', async () => {
    grant();
    await registerForPush();
    expect(n.getExpoPushTokenAsync).toHaveBeenCalledWith({ projectId: PROJECT });
    expect(deviceCalls()).toHaveLength(1);
    const request = deviceCalls()[0][0] as Request;
    expect(request.method).toBe('POST');
    expect(request.url).toBe('http://proxy.test/notify/devices');
    expect(request.headers.get('Authorization')).toBe('Bearer jwt');
    expect(await request.clone().json()).toEqual({ expoPushToken: TOKEN, platform: 'android' });
  });

  it('sends platform ios on iOS and creates no Android channel there', async () => {
    grant();
    Platform.OS = 'ios';
    await registerForPush();
    expect(await (deviceCalls()[0][0] as Request).clone().json()).toEqual({ expoPushToken: TOKEN, platform: 'ios' });
    expect(n.setNotificationChannelAsync).not.toHaveBeenCalled();
  });

  it('creates the Android channel before asking for permission', async () => {
    grant();
    await registerForPush();
    expect(n.setNotificationChannelAsync).toHaveBeenCalledWith('default', expect.objectContaining({ name: 'default' }));
    expect(n.setNotificationChannelAsync.mock.invocationCallOrder[0]).toBeLessThan(
      n.getPermissionsAsync.mock.invocationCallOrder[0],
    );
  });

  it('does nothing on web', async () => {
    grant();
    Platform.OS = 'web';
    await registerForPush();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it.each([400, 401, 403, 500])('a %i from /notify/devices is silent and does not end the session', async (status) => {
    grant();
    fetchMock.mockResolvedValue(new Response(JSON.stringify({}), { status, headers: { 'Content-Type': 'application/json' } }));
    const alert = jest.spyOn(Alert, 'alert');
    await expect(registerForPush()).resolves.toBeUndefined();
    expect(onUnauthorized).not.toHaveBeenCalled();
    expect(SecureStore.deleteItemAsync).not.toHaveBeenCalled();
    expect(alert).not.toHaveBeenCalled();
  });

  it('a network error is silent', async () => {
    grant();
    fetchMock.mockRejectedValue(new TypeError('Network request failed'));
    await expect(registerForPush()).resolves.toBeUndefined();
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('a failing token request or permission check is silent', async () => {
    grant();
    n.getExpoPushTokenAsync.mockRejectedValue(new Error('no google play services'));
    await expect(registerForPush()).resolves.toBeUndefined();
    n.getPermissionsAsync.mockRejectedValue(new Error('boom'));
    await expect(registerForPush()).resolves.toBeUndefined();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
