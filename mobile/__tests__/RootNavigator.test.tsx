import React from 'react';
import { render, screen } from '@testing-library/react-native';
import { NavigationContainer } from '@react-navigation/native';
jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn() }));
import * as SecureStore from 'expo-secure-store';
import { RootNavigator } from '../src/navigation/RootNavigator';

async function renderApp() {
  await render(
    <NavigationContainer>
      <RootNavigator />
    </NavigationContainer>,
  );
}

const getItem = SecureStore.getItemAsync as jest.Mock;

describe('RootNavigator', () => {
  beforeEach(() => getItem.mockReset().mockResolvedValue(null));

  it('renders nothing until the stored token has been read', async () => {
    getItem.mockReturnValue(new Promise(() => {}));
    await renderApp();
    expect(screen.queryByTestId('login-screen')).toBeNull();
    expect(screen.queryByTestId('pending-approvals-screen')).toBeNull();
  });

  it('starts on PendingApprovals when a token is stored', async () => {
    getItem.mockResolvedValue('stored-jwt');
    await renderApp();
    expect(await screen.findByTestId('pending-approvals-screen')).toBeTruthy();
    expect(screen.queryByTestId('login-screen')).toBeNull();
  });

  it('starts on Login when no token is stored', async () => {
    await renderApp();
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
    expect(screen.queryByTestId('pending-approvals-screen')).toBeNull();
  });

  it('starts on Login when reading the token fails', async () => {
    getItem.mockRejectedValue(new Error('keystore'));
    await renderApp();
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
  });

  it('opens on the login screen', async () => {
    await renderApp();
    expect(await screen.findByTestId('login-screen')).toBeTruthy();
    expect(await screen.findByText('Sign in')).toBeTruthy();
  });

  it('does not show the other screens at start', async () => {
    await renderApp();
    await screen.findByTestId('login-screen');
    expect(screen.queryByTestId('pending-approvals-screen')).toBeNull();
    expect(screen.queryByTestId('request-detail-screen')).toBeNull();
  });
});
