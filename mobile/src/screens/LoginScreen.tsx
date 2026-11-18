import React, { useRef, useState } from 'react';
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { RootStackParamList } from '../navigation/types';
import { login } from '../api/generated/servicedesk';
import { tokenStore } from '../auth/tokenStore';
import { loginErrorMessage } from '../auth/loginErrors';

type Props = NativeStackScreenProps<RootStackParamList, 'Login'>;

// Emails only. The shared demo password is never in the app.
const DEMO_ACCOUNTS = [
  'requester@servicedesk.local',
  'ops.head@servicedesk.local',
  'it.manager@servicedesk.local',
  'admin@servicedesk.local',
];

export function LoginScreen({ navigation }: Props) {
  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const passwordRef = useRef<React.ComponentRef<typeof TextInput>>(null);

  const canSubmit = identifier.trim() !== '' && password !== '' && !pending;

  async function signIn() {
    // The ref is set synchronously, so a keyboard submit and a tap in the same frame cannot both pass.
    if (inFlight.current || !canSubmit) return;
    inFlight.current = true;
    setPending(true);
    setError(null);
    let succeeded = false;
    try {
      const result = await login({ body: { identifier: identifier.trim(), password } });
      if (result.data) {
        await tokenStore.set(result.data.accessToken);
        navigation.replace('PendingApprovals'); // replace, so Back does not return to Login
        succeeded = true;
        return;
      }
      setError(loginErrorMessage(result.response?.status, result.error));
    } catch {
      // The client reports network failures as `error`, so a throw here is storage or navigation.
      // Do not leave a token behind if we could not leave the screen.
      await tokenStore.clear().catch(() => undefined);
      setError(loginErrorMessage(500));
    } finally {
      inFlight.current = false;
      if (!succeeded) setPending(false);
    }
  }

  return (
    <View testID="login-screen" style={styles.container}>
      <Text style={styles.label}>Email or employee number</Text>
      <TextInput
        testID="login-identifier"
        style={styles.input}
        value={identifier}
        onChangeText={setIdentifier}
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="email-address"
        autoComplete="username"
        returnKeyType="next"
        onSubmitEditing={() => passwordRef.current?.focus()}
        editable={!pending}
      />
      <Text style={styles.label}>Password</Text>
      <TextInput
        testID="login-password"
        ref={passwordRef}
        style={styles.input}
        value={password}
        onChangeText={setPassword}
        secureTextEntry
        autoComplete="password"
        autoCapitalize="none"
        autoCorrect={false}
        returnKeyType="go"
        onSubmitEditing={signIn}
        editable={!pending}
      />
      {error ? (
        <Text testID="login-error" accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
          {error}
        </Text>
      ) : null}
      <Pressable
        testID="login-submit"
        accessibilityRole="button"
        accessibilityState={{ disabled: !canSubmit }}
        disabled={!canSubmit}
        onPress={signIn}
        style={[styles.button, !canSubmit && styles.buttonDisabled]}
      >
        {pending ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Sign in</Text>}
      </Pressable>
      {__DEV__ ? (
        <View testID="login-demo-hint" style={styles.hint}>
          <Text style={styles.hintTitle}>Demo accounts (Development only)</Text>
          {DEMO_ACCOUNTS.map((email) => (
            <Text key={email} style={styles.hintLine}>
              {email}
            </Text>
          ))}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 24, gap: 8 },
  label: { fontSize: 14, fontWeight: '600' },
  input: { borderWidth: 1, borderColor: '#999', borderRadius: 6, padding: 10, fontSize: 16 },
  error: { color: '#b00020', marginTop: 4 },
  button: { backgroundColor: '#1a56db', borderRadius: 6, padding: 12, alignItems: 'center', marginTop: 12 },
  buttonDisabled: { opacity: 0.5 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  hint: { marginTop: 24 },
  hintTitle: { fontSize: 12, fontWeight: '600', color: '#555' },
  hintLine: { fontSize: 12, color: '#555' },
});
