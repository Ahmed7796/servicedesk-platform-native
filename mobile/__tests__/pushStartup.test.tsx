import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { NavigationContainer } from '@react-navigation/native';
import { Platform } from 'react-native';
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn().mockResolvedValue(undefined),
  deleteItemAsync: jest.fn().mockResolvedValue(undefined),
}));
import * as SecureStore from 'expo-secure-store';
import { configureApi } from '../src/api/config';
import { RootNavigator } from '../src/navigation/RootNavigator';

const originalFetch = globalThis.fetch;
const originalOS = Platform.OS;
const fetchMock = jest.fn();
const getItem = SecureStore.getItemAsync as jest.Mock;
const n = jest.mocked(Notifications);
const constants = Constants as unknown as { expoConfig: unknown };
const json = (body: unknown) =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
const deviceCalls = () => fetchMock.mock.calls.filter((c) => (c[0] as Request).url.endsWith('/notify/devices'));

beforeEach(() => {
  fetchMock.mockReset().mockImplementation((request: Request) => {
    if (request.url.endsWith('/api/auth/login')) return json({ accessToken: 'jwt-new', expiresAt: '2026-11-19T12:00:00Z' });
    if (request.url.endsWith('/notify/devices')) return Promise.resolve(new Response(null, { status: 204 }));
    return json([]); // pending-for-me and overdue
  });
  getItem.mockReset().mockResolvedValue(null);
  n.getPermissionsAsync.mockReset().mockResolvedValue({ status: 'granted' } as never);
  n.getExpoPushTokenAsync.mockReset().mockResolvedValue({ data: 'ExponentPushToken[abc]', type: 'expo' } as never);
  constants.expoConfig = { extra: { eas: { projectId: '44444444-4444-4444-8444-444444444444' } } };
  Platform.OS = 'android';
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
  Platform.OS = originalOS;
});

const renderApp = () =>
  render(
    <NavigationContainer>
      <RootNavigator />
    </NavigationContainer>,
  );

async function signIn() {
  await fireEvent.changeText(await screen.findByTestId('login-identifier'), 'requester@servicedesk.local');
  await fireEvent.changeText(screen.getByTestId('login-password'), 'pw');
  await fireEvent.press(screen.getByTestId('login-submit'));
}

describe('push registration timing', () => {
  it('registers on start when a token is stored', async () => {
    getItem.mockResolvedValue('jwt');
    await renderApp();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    await waitFor(() => expect(deviceCalls()).toHaveLength(1));
  });

  it('does not register on start without a stored token', async () => {
    await renderApp();
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(n.getPermissionsAsync).not.toHaveBeenCalled();
    expect(deviceCalls()).toHaveLength(0);
  });

  it('registers once after a successful login', async () => {
    await renderApp();
    await signIn();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    await waitFor(() => expect(deviceCalls()).toHaveLength(1));
  });

  it('does not register after a failed login', async () => {
    fetchMock.mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify({}), { status: 401, headers: { 'Content-Type': 'application/json' } })),
    );
    await renderApp();
    await signIn();
    await screen.findByTestId('login-error');
    expect(n.getPermissionsAsync).not.toHaveBeenCalled();
  });

  it('does not delay navigation: PendingApprovals shows while the permission check never answers', async () => {
    n.getPermissionsAsync.mockReturnValue(new Promise(() => {}));
    await renderApp();
    await signIn();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    expect(screen.queryByTestId('login-screen')).toBeNull();
  });

  it('does not delay the start screen either', async () => {
    getItem.mockResolvedValue('jwt');
    n.getPermissionsAsync.mockReturnValue(new Promise(() => {}));
    await renderApp();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
  });

  it('a failed registration leaves the signed-in app working', async () => {
    getItem.mockResolvedValue('jwt');
    n.getExpoPushTokenAsync.mockRejectedValue(new Error('no fcm'));
    await renderApp();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    expect(screen.getByTestId('sign-out')).toBeTruthy();
  });
});
