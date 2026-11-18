import React from 'react';
import { Text, View } from 'react-native';

// Shown by App instead of the navigator. Not a screen: the app has exactly three.
export function ConfigError() {
  return (
    <View testID="config-error" style={{ flex: 1, justifyContent: 'center', padding: 24 }}>
      <Text style={{ fontSize: 18, fontWeight: '600' }}>App is not configured</Text>
      <Text>
        EXPO_PUBLIC_API_BASE_URL is not set. Set it to the reverse-proxy URL (see .env.example) and
        restart the app.
      </Text>
    </View>
  );
}
