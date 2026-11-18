import React from 'react';
import { NavigationContainer } from '@react-navigation/native';
import { RootNavigator } from './src/navigation/RootNavigator';
import { ConfigError } from './src/components/ConfigError';
import { configureApi, getBaseUrl } from './src/api/config';
import { tokenStore } from './src/auth/tokenStore';

export default function App() {
  const baseUrl = getBaseUrl();
  if (!baseUrl) return <ConfigError />;
  configureApi(baseUrl, tokenStore.get);
  return (
    <NavigationContainer>
      <RootNavigator />
    </NavigationContainer>
  );
}
