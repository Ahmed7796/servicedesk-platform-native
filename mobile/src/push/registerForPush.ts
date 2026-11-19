import { Platform } from 'react-native';
import * as Notifications from 'expo-notifications';
import { registerDevice } from '../api/generated/notification';
import { getProjectId } from './projectId';

/**
 * Registers this phone for push: channel, permission, Expo token, then POST /notify/devices.
 * Best effort and silent: it never rejects and never shows anything. A missing projectId, a denied
 * permission, a failed token request or any failed call just ends it. Callers use `void`, so it
 * never delays navigation. A 401 here does NOT end the session (only the API's 401 does).
 * Not cached: the endpoint is idempotent, and the same phone may be signed into by another user.
 */
export async function registerForPush(): Promise<void> {
  try {
    const platform = Platform.OS;
    if (platform !== 'ios' && platform !== 'android') return;
    const projectId = getProjectId();
    if (!projectId) {
      if (__DEV__) console.warn('Push registration skipped: no EAS projectId (run `eas init`).');
      return;
    }
    if (platform === 'android') {
      await Notifications.setNotificationChannelAsync('default', {
        name: 'default',
        importance: Notifications.AndroidImportance.MAX,
      });
    }
    let { status } = await Notifications.getPermissionsAsync();
    if (status !== 'granted') ({ status } = await Notifications.requestPermissionsAsync());
    if (status !== 'granted') return;
    const { data: expoPushToken } = await Notifications.getExpoPushTokenAsync({ projectId });
    await registerDevice({ body: { expoPushToken, platform } });
  } catch {
    // Silent by design.
  }
}
