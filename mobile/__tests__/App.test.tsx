import React from 'react';
import { render, screen } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({}));
import App from '../App';

describe('App configuration', () => {
  const original = process.env.EXPO_PUBLIC_API_BASE_URL;
  afterEach(() => {
    if (original === undefined) delete process.env.EXPO_PUBLIC_API_BASE_URL;
    else process.env.EXPO_PUBLIC_API_BASE_URL = original;
  });

  it('shows a configuration error instead of the navigator when the base URL is missing', async () => {
    delete process.env.EXPO_PUBLIC_API_BASE_URL;
    await render(<App />);
    expect(screen.getByTestId('config-error')).toBeTruthy();
    expect(screen.queryByTestId('login-screen')).toBeNull();
  });

  it('reaches the login screen when the base URL is set', async () => {
    process.env.EXPO_PUBLIC_API_BASE_URL = 'http://proxy:8000';
    await render(<App />);
    expect(screen.getByTestId('login-screen')).toBeTruthy();
  });
});
