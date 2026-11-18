import React from 'react';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import type { RootStackParamList } from './types';
import { LoginScreen } from '../screens/LoginScreen';
import { PendingApprovalsScreen } from '../screens/PendingApprovalsScreen';
import { RequestDetailScreen } from '../screens/RequestDetailScreen';

const Stack = createNativeStackNavigator<RootStackParamList>();

// Three screens only (CLAUDE.md). Login is the initial route.
export function RootNavigator() {
  return (
    <Stack.Navigator initialRouteName="Login">
      <Stack.Screen name="Login" component={LoginScreen} options={{ title: 'Sign in' }} />
      <Stack.Screen
        name="PendingApprovals"
        component={PendingApprovalsScreen}
        options={{ title: 'Pending approvals' }}
      />
      <Stack.Screen
        name="RequestDetail"
        component={RequestDetailScreen}
        options={{ title: 'Request' }}
      />
    </Stack.Navigator>
  );
}
