import React, { useEffect, useState } from 'react';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import type { RootStackParamList } from './types';
import { LoginScreen } from '../screens/LoginScreen';
import { PendingApprovalsScreen } from '../screens/PendingApprovalsScreen';
import { RequestDetailScreen } from '../screens/RequestDetailScreen';
import { SignOutButton } from '../components/SignOutButton';
import { tokenStore } from '../auth/tokenStore';

const Stack = createNativeStackNavigator<RootStackParamList>();

// Three screens only (CLAUDE.md). Login is the initial route unless a token is already stored.
// The token is read once; until it resolves nothing renders, so a signed-in user never sees Login flash.
export function RootNavigator() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null);
  useEffect(() => {
    tokenStore
      .get()
      .then((token) => !!token, () => false)
      .then(setSignedIn);
  }, []);
  if (signedIn === null) return null;
  return (
    <Stack.Navigator initialRouteName={signedIn ? 'PendingApprovals' : 'Login'}>
      <Stack.Screen name="Login" component={LoginScreen} options={{ title: 'Sign in' }} />
      <Stack.Screen
        name="PendingApprovals"
        component={PendingApprovalsScreen}
        options={{ title: 'Pending approvals', headerRight: () => <SignOutButton /> }}
      />
      <Stack.Screen
        name="RequestDetail"
        component={RequestDetailScreen}
        options={{ title: 'Request' }}
      />
    </Stack.Navigator>
  );
}
