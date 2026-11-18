import React from 'react';
import { render, screen } from '@testing-library/react-native';
import { NavigationContainer } from '@react-navigation/native';
import { RootNavigator } from '../src/navigation/RootNavigator';

async function renderApp() {
  await render(
    <NavigationContainer>
      <RootNavigator />
    </NavigationContainer>,
  );
}

describe('RootNavigator', () => {
  it('opens on the login screen', async () => {
    await renderApp();
    expect(screen.getByTestId('login-screen')).toBeTruthy();
    expect(screen.getByText('Sign in')).toBeTruthy();
  });

  it('does not show the other screens at start', async () => {
    await renderApp();
    expect(screen.queryByTestId('pending-approvals-screen')).toBeNull();
    expect(screen.queryByTestId('request-detail-screen')).toBeNull();
  });
});
