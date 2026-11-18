import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

const mockStore = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(async (k: string) => mockStore.get(k) ?? null),
  setItemAsync: jest.fn(async (k: string, v: string) => void mockStore.set(k, v)),
  deleteItemAsync: jest.fn(async (k: string) => void mockStore.delete(k)),
}));
import * as SecureStore from 'expo-secure-store';
import { SignOutButton } from '../src/components/SignOutButton';
import { navigationRef, signOut } from '../src/auth/session';
import { tokenStore } from '../src/auth/tokenStore';

let reset: jest.SpyInstance;
beforeEach(async () => {
  mockStore.clear();
  (SecureStore.deleteItemAsync as jest.Mock).mockClear();
  jest.spyOn(navigationRef, 'isReady').mockReturnValue(true);
  reset = jest.spyOn(navigationRef, 'reset').mockImplementation(() => undefined);
  await tokenStore.set('jwt');
});
afterEach(() => jest.restoreAllMocks());

describe('sign out', () => {
  it('clears the token and resets to Login', async () => {
    await render(<SignOutButton />);
    await fireEvent.press(screen.getByTestId('sign-out'));
    await waitFor(() => expect(reset).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'Login' }] }));
    expect(await tokenStore.get()).toBeNull();
  });

  it('retries the clear once; if the retry works there is no notice', async () => {
    (SecureStore.deleteItemAsync as jest.Mock).mockRejectedValueOnce(new Error('keystore busy'));
    await expect(signOut()).resolves.toBeUndefined();
    expect(SecureStore.deleteItemAsync).toHaveBeenCalledTimes(2);
    expect(await tokenStore.get()).toBeNull();
    expect(reset).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'Login', params: undefined }] });
  });

  it('when the clear fails twice it still resets to Login with a notice and never rejects', async () => {
    const original = (SecureStore.deleteItemAsync as jest.Mock).getMockImplementation();
    (SecureStore.deleteItemAsync as jest.Mock).mockRejectedValue(new Error('keystore broken'));
    const unhandled = jest.fn();
    process.on('unhandledRejection', unhandled);
    try {
      await render(<SignOutButton />);
      await fireEvent.press(screen.getByTestId('sign-out')); // the button calls signOut() without awaiting
      await waitFor(() => expect(reset).toHaveBeenCalledTimes(1));
      await new Promise((r) => setTimeout(r, 20)); // let any unhandled rejection surface
    } finally {
      process.off('unhandledRejection', unhandled);
      (SecureStore.deleteItemAsync as jest.Mock).mockImplementation(original);
    }
    expect(SecureStore.deleteItemAsync).toHaveBeenCalledTimes(2);
    expect(reset).toHaveBeenCalledWith({
      index: 0,
      routes: [{ name: 'Login', params: { notice: "Couldn't remove your sign-in from this phone. Sign in and sign out again to retry." } }],
    });
    expect(unhandled).not.toHaveBeenCalled();
    await expect(signOut()).resolves.toBeUndefined();
  });
});
