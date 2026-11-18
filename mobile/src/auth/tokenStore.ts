import * as SecureStore from 'expo-secure-store';

const KEY = 'servicedesk.accessToken';

// The JWT lives in the platform keystore only, never in AsyncStorage.
export const tokenStore = {
  get: (): Promise<string | null> => SecureStore.getItemAsync(KEY),
  set: (token: string): Promise<void> => SecureStore.setItemAsync(KEY, token),
  clear: (): Promise<void> => SecureStore.deleteItemAsync(KEY),
};
