import React from 'react';
import { Pressable, StyleSheet, Text } from 'react-native';
import { signOut } from '../auth/session';

// A header button, not a screen.
export function SignOutButton() {
  return (
    <Pressable testID="sign-out" accessibilityRole="button" accessibilityLabel="Sign out" onPress={() => void signOut()}>
      <Text style={styles.text}>Sign out</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({ text: { color: '#1a56db', fontSize: 16 } });
