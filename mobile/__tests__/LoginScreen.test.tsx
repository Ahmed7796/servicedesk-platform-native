import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn().mockResolvedValue(undefined),
  deleteItemAsync: jest.fn().mockResolvedValue(undefined),
}));
import * as SecureStore from 'expo-secure-store';
import { configureApi } from '../src/api/config';
import { LoginScreen } from '../src/screens/LoginScreen';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const replace = jest.fn();

function json(status: number, body: unknown, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } });
}

async function renderScreen() {
  const navigation = { replace } as never;
  await render(<LoginScreen navigation={navigation} route={{ key: 'k', name: 'Login' }} />);
}

async function fill(identifier = 'requester@servicedesk.local', password = 'pw') {
  await fireEvent.changeText(screen.getByTestId('login-identifier'), identifier);
  await fireEvent.changeText(screen.getByTestId('login-password'), password);
}

beforeEach(() => {
  jest.clearAllMocks();
  replace.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => null);
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('LoginScreen', () => {
  it('stores the token and replaces the route with PendingApprovals on success', async () => {
    fetchMock.mockResolvedValue(json(200, { accessToken: 'jwt-123', expiresAt: '2026-11-18T12:00:00Z' }));
    await renderScreen();
    await fill('  requester@servicedesk.local  ', ' pw ');
    await fireEvent.press(screen.getByTestId('login-submit'));

    await waitFor(() => expect(replace).toHaveBeenCalledWith('PendingApprovals'));
    expect(SecureStore.setItemAsync).toHaveBeenCalledWith(expect.any(String), 'jwt-123');
    const request = fetchMock.mock.calls[0][0] as Request;
    expect(request.url).toBe('http://proxy.test/api/auth/login');
    // Identifier is trimmed, the password is sent exactly as typed.
    expect(await request.json()).toEqual({ identifier: 'requester@servicedesk.local', password: ' pw ' });
  });

  it('401 shows the wrong-credentials message and stores nothing', async () => {
    fetchMock.mockResolvedValue(json(401, { title: 'Unauthorized', status: 401 }, 'application/problem+json'));
    await renderScreen();
    await fill();
    await fireEvent.press(screen.getByTestId('login-submit'));

    expect((await screen.findByTestId('login-error')).props.children).toMatch(/wrong .*password/i);
    expect(SecureStore.setItemAsync).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });

  it('400 shows the validation message', async () => {
    fetchMock.mockResolvedValue(
      json(400, { title: 'Validation', status: 400, errors: { Identifier: ['Identifier is required.'] } }, 'application/problem+json'),
    );
    await renderScreen();
    await fill();
    await fireEvent.press(screen.getByTestId('login-submit'));

    expect((await screen.findByTestId('login-error')).props.children).toBe('Identifier is required.');
    expect(SecureStore.setItemAsync).not.toHaveBeenCalled();
  });

  it('a network failure shows the unreachable message, with no raw error text', async () => {
    fetchMock.mockRejectedValue(new TypeError('Network request failed'));
    await renderScreen();
    await fill();
    await fireEvent.press(screen.getByTestId('login-submit'));

    const text = (await screen.findByTestId('login-error')).props.children;
    expect(text).toMatch(/can't reach the server/i);
    expect(text).not.toMatch(/TypeError|Network request failed/);
    expect(SecureStore.setItemAsync).not.toHaveBeenCalled();
  });

  it('disables the button while signing in and enables it again after a failure', async () => {
    let resolve!: (r: Response) => void;
    fetchMock.mockReturnValue(new Promise<Response>((r) => (resolve = r)));
    await renderScreen();

    expect(screen.getByTestId('login-submit').props.accessibilityState.disabled).toBe(true); // empty fields
    await fill();
    expect(screen.getByTestId('login-submit').props.accessibilityState.disabled).toBe(false);

    void fireEvent.press(screen.getByTestId('login-submit'));
    await waitFor(() => expect(screen.getByTestId('login-submit').props.accessibilityState.disabled).toBe(true));
    void fireEvent.press(screen.getByTestId('login-submit'));
    await act(async () => {}); // flush, so a second signIn would have reached fetch by now
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => resolve(json(401, { title: 'Unauthorized', status: 401 }, 'application/problem+json')));
    await waitFor(() => expect(screen.getByTestId('login-submit').props.accessibilityState.disabled).toBe(false));
  });

  it('submits from the keyboard on the password field', async () => {
    fetchMock.mockResolvedValue(json(200, { accessToken: 't', expiresAt: '2026-11-18T12:00:00Z' }));
    await renderScreen();
    await fill();
    await fireEvent(screen.getByTestId('login-password'), 'submitEditing');
    await waitFor(() => expect(replace).toHaveBeenCalledWith('PendingApprovals'));
  });

  it('ignores a second submit in the same frame as the first', async () => {
    let resolve!: (r: Response) => void;
    fetchMock.mockReturnValue(new Promise<Response>((r) => (resolve = r)));
    await renderScreen();
    await fill();
    const password = screen.getByTestId('login-password');
    // Both calls run before React re-renders, so only the in-flight ref can stop the second one.
    await act(async () => {
      password.props.onSubmitEditing();
      password.props.onSubmitEditing();
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    // Settle the request so no act() scope leaks into the next test.
    await act(async () => resolve(json(401, { title: 'Unauthorized', status: 401 }, 'application/problem+json')));
  });

  it('shows the generic message and leaves nothing stored when navigation throws', async () => {
    fetchMock.mockResolvedValue(json(200, { accessToken: 'jwt-123', expiresAt: '2026-11-18T12:00:00Z' }));
    replace.mockImplementationOnce(() => {
      throw new Error('boom: internal detail');
    });
    await renderScreen();
    await fill();
    await fireEvent.press(screen.getByTestId('login-submit'));

    const text = (await screen.findByTestId('login-error')).props.children;
    expect(text).toBe('Something went wrong. Please try again.');
    expect(SecureStore.deleteItemAsync).toHaveBeenCalled();
    expect(screen.getByTestId('login-submit').props.accessibilityState.disabled).toBe(false);
  });
});
