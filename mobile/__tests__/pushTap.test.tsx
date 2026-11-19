import React from 'react';
import { act, render, screen, waitFor } from '@testing-library/react-native';
import { NavigationContainer, CommonActions } from '@react-navigation/native';
import * as Notifications from 'expo-notifications';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn().mockResolvedValue(undefined),
  deleteItemAsync: jest.fn().mockResolvedValue(undefined),
}));
import * as SecureStore from 'expo-secure-store';
import { configureApi } from '../src/api/config';
import { RootNavigator } from '../src/navigation/RootNavigator';
import { navigationRef } from '../src/auth/session';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const getItem = SecureStore.getItemAsync as jest.Mock;
const n = jest.mocked(Notifications);
// Registered when the module loads (before any beforeEach clears the mock).
const foregroundHandler = n.setNotificationHandler.mock.calls[0]?.[0];
const A = '11111111-1111-4111-8111-111111111111';
const B = '55555555-5555-4555-8555-555555555555';
const USER = '22222222-2222-4222-8222-222222222222';
const DEPT = '33333333-3333-4333-8333-333333333333';
let seq = 0;

const json = (body: unknown) =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
const detailFor = (id: string) => ({
  id, title: `Title ${id.slice(0, 2)}`, description: 'd', type: 'SoftwareAccess', status: 'Approved',
  requesterId: USER, departmentId: DEPT, createdAt: '2026-11-16T10:00:00Z', updatedAt: '2026-11-16T11:00:00Z',
  steps: [], requesterName: 'Rana', auditTrail: [], triage: null,
});
const count = (part: string) => fetchMock.mock.calls.filter((c) => (c[0] as Request).url.includes(part)).length;
const detailCalls = () =>
  fetchMock.mock.calls.filter((c) => /\/service-requests\/[0-9a-f-]{36}$/.test(new URL((c[0] as Request).url).pathname)).length;
const routeNames = () => (navigationRef.getRootState()?.routes ?? []).map((r) => r.name);

// A notification as expo-notifications delivers it; a fresh identifier each time.
const notification = (data: unknown, identifier = `id-${++seq}`) => ({ request: { identifier, content: { data } } });
const response = (data: unknown, identifier?: string) => ({ notification: notification(data, identifier), actionIdentifier: 'default' });

const onResponse = () => n.addNotificationResponseReceivedListener.mock.calls.at(-1)![0] as (r: unknown) => void;
const onReceived = () => n.addNotificationReceivedListener.mock.calls.at(-1)![0] as (x: unknown) => void;
const tap = (data: unknown) => act(async () => onResponse()(response(data)));

const renderApp = () =>
  render(
    <NavigationContainer ref={navigationRef}>
      <RootNavigator />
    </NavigationContainer>,
  );

beforeEach(() => {
  fetchMock.mockReset().mockImplementation((request: Request) => {
    const m = /\/api\/service-requests\/([^/]+)$/.exec(new URL(request.url).pathname);
    if (m) return json(detailFor(m[1]));
    return json([]); // pending-for-me, overdue
  });
  getItem.mockReset().mockResolvedValue('jwt');
  n.addNotificationReceivedListener.mockClear();
  n.addNotificationResponseReceivedListener.mockClear();
  n.getLastNotificationResponse.mockReset().mockReturnValue(null);
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('tapping a notification', () => {
  it('opens RequestDetail for the request, with one RequestDetail entry above PendingApprovals', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await tap({ requestId: A, type: 'request.submitted' });
    expect(await screen.findByText(`Title ${A.slice(0, 2)}`)).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
    expect(navigationRef.getCurrentRoute()?.params).toEqual({ requestId: A });
  });

  it('a second tap for the same request while it is open adds no entry', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await tap({ requestId: A });
    await screen.findByText(`Title ${A.slice(0, 2)}`);
    await tap({ requestId: A });
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
  });

  it('a tap for another request while one is open shows the new one in the same entry', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await tap({ requestId: A });
    await screen.findByText(`Title ${A.slice(0, 2)}`);
    await tap({ requestId: B });
    expect(await screen.findByText(`Title ${B.slice(0, 2)}`)).toBeTruthy();
    expect(screen.queryByText(`Title ${A.slice(0, 2)}`)).toBeNull();
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
    expect(navigationRef.getCurrentRoute()?.params).toEqual({ requestId: B });
  });

  it.each([
    ['missing requestId', { type: 'request.submitted' }],
    ['not a uuid', { requestId: 'nope' }],
    ['a number', { requestId: 7 }],
    ['no data at all', undefined],
  ])('does nothing for %s', async (_name, data) => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await tap(data);
    expect(routeNames()).toEqual(['PendingApprovals']);
    expect(detailCalls()).toBe(0);
  });

  it('while signed out goes to Login and drops the link', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    getItem.mockResolvedValue(null); // the token is gone by the time of the tap
    await tap({ requestId: A });
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
    expect(routeNames()).toEqual(['Login']);
    expect(detailCalls()).toBe(0);
  });

  it('signed in but still showing Login builds the normal stack, so Back reaches the list', async () => {
    getItem.mockResolvedValue(null);
    await renderApp();
    await screen.findByTestId('login-screen');
    getItem.mockResolvedValue('jwt'); // signed in since, without a restart
    await tap({ requestId: A });
    expect(await screen.findByText(`Title ${A.slice(0, 2)}`)).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
  });

  it('while signed out and already on Login stays on Login', async () => {
    getItem.mockResolvedValue(null);
    await renderApp();
    await screen.findByTestId('login-screen');
    await tap({ requestId: A });
    expect(routeNames()).toEqual(['Login']);
  });

  it('reading the token failing counts as signed out', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    getItem.mockRejectedValue(new Error('keystore'));
    await tap({ requestId: A });
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
  });
});

describe('opened from a notification (cold start)', () => {
  it('opens RequestDetail once, and a remount does not replay it', async () => {
    n.getLastNotificationResponse.mockReturnValue(response({ requestId: A }, 'cold-1') as never);
    const first = await renderApp();
    expect(await screen.findByText(`Title ${A.slice(0, 2)}`)).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
    await first.unmount();

    await renderApp(); // same last response is still reported
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals']);
  });

  it('ignores an invalid last response', async () => {
    n.getLastNotificationResponse.mockReturnValue(response({ requestId: 'x' }, 'cold-2') as never);
    await renderApp();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals']);
  });

  it('without a stored token goes to Login and drops the link', async () => {
    getItem.mockResolvedValue(null);
    n.getLastNotificationResponse.mockReturnValue(response({ requestId: A }, 'cold-3') as never);
    await renderApp();
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
    expect(routeNames()).toEqual(['Login']);
  });
});

describe('a tap before the navigator is ready', () => {
  it('is kept and opens once the navigator reports state, and only once', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    const notReady = jest.spyOn(navigationRef, 'isReady').mockReturnValue(false);
    await tap({ requestId: A });
    expect(routeNames()).toEqual(['PendingApprovals']); // nothing yet
    notReady.mockRestore();
    await act(async () => {
      navigationRef.dispatch(CommonActions.setParams({ refresh: 1 })); // a state event
    });
    expect(await screen.findByText(`Title ${A.slice(0, 2)}`)).toBeTruthy();
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
    await act(async () => {
      navigationRef.dispatch(CommonActions.setParams({ requestId: A }));
    });
    expect(routeNames()).toEqual(['PendingApprovals', 'RequestDetail']);
    expect(detailCalls()).toBe(1);
  });

  it('is dropped, with no leftover waiting, if the app unmounts first', async () => {
    const view = await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    const notReady = jest.spyOn(navigationRef, 'isReady').mockReturnValue(false);
    await tap({ requestId: A });
    await view.unmount();
    notReady.mockRestore();
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await act(async () => {
      navigationRef.dispatch(CommonActions.setParams({ refresh: 2 }));
    });
    expect(routeNames()).toEqual(['PendingApprovals']);
    expect(detailCalls()).toBe(0);
  });
});

describe('a notification while the app is open', () => {
  it('shows a banner and a list entry, with no sound or badge', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await expect(foregroundHandler!.handleNotification(notification({}) as never)).resolves.toEqual({
      shouldShowBanner: true, shouldShowList: true, shouldPlaySound: false, shouldSetBadge: false,
    });
  });

  it('on PendingApprovals with a valid requestId reloads the list', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await waitFor(() => expect(count('/pending-for-me')).toBe(1));
    await act(async () => onReceived()(notification({ requestId: A })));
    await waitFor(() => expect(count('/pending-for-me')).toBe(2));
    expect(routeNames()).toEqual(['PendingApprovals']);
  });

  it('with an invalid or missing requestId does not reload', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await waitFor(() => expect(count('/pending-for-me')).toBe(1));
    await act(async () => onReceived()(notification({ requestId: 'bad' })));
    await act(async () => onReceived()(notification({})));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(count('/pending-for-me')).toBe(1);
  });

  it('on RequestDetail does not reload the list or change the screen', async () => {
    await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    await tap({ requestId: A });
    await screen.findByText(`Title ${A.slice(0, 2)}`);
    const before = count('/pending-for-me');
    await act(async () => onReceived()(notification({ requestId: B })));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(count('/pending-for-me')).toBe(before);
    expect(navigationRef.getCurrentRoute()?.params).toEqual({ requestId: A });
  });

  it('on Login does nothing', async () => {
    getItem.mockResolvedValue(null);
    await renderApp();
    await screen.findByTestId('login-screen');
    await act(async () => onReceived()(notification({ requestId: A })));
    expect(routeNames()).toEqual(['Login']);
    expect(count('/pending-for-me')).toBe(0);
  });
});

describe('listeners', () => {
  it('are added once and removed on unmount', async () => {
    const view = await renderApp();
    await screen.findByTestId('pending-approvals-screen');
    expect(n.addNotificationReceivedListener).toHaveBeenCalledTimes(1);
    expect(n.addNotificationResponseReceivedListener).toHaveBeenCalledTimes(1);
    const received = n.addNotificationReceivedListener.mock.results[0].value as { remove: jest.Mock };
    const responded = n.addNotificationResponseReceivedListener.mock.results[0].value as { remove: jest.Mock };
    expect(received.remove).not.toHaveBeenCalled();
    await view.unmount();
    expect(received.remove).toHaveBeenCalledTimes(1);
    expect(responded.remove).toHaveBeenCalledTimes(1);
  });
});
