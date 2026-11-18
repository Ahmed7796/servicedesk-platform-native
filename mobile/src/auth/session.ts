import { createNavigationContainerRef } from '@react-navigation/native';
import type { RootStackParamList } from '../navigation/types';
import { tokenStore } from './tokenStore';

export const navigationRef = createNavigationContainerRef<RootStackParamList>();

const SIGN_OUT_FAILED = "Couldn't remove your sign-in from this phone. Sign in and sign out again to retry.";

/**
 * The user chose to sign out: clear the token (one retry), then go back to Login. If the token
 * still cannot be cleared, Login shows a notice. Never rejects.
 */
export async function signOut(): Promise<void> {
  let cleared = false;
  for (let attempt = 0; attempt < 2 && !cleared; attempt++) {
    try {
      await tokenStore.clear();
      cleared = true;
    } catch {
      // retry once, then report on Login
    }
  }
  if (navigationRef.isReady()) {
    navigationRef.reset({ index: 0, routes: [{ name: 'Login', params: cleared ? undefined : { notice: SIGN_OUT_FAILED } }] });
  }
}

let inFlight: Promise<void> | null = null;

async function endSession(failedToken: string | null): Promise<void> {
  // End the session only if the request that failed was sent with the token we still hold.
  // No token, or a different one, means this 401 is stale (an earlier 401 already ended that
  // session, and the user may have signed in again since). There is no refresh endpoint:
  // a 401 for the current token always means "sign in again".
  const stored = await tokenStore.get().catch(() => null);
  if (!stored || stored !== failedToken) return;
  await tokenStore.clear();
  if (navigationRef.isReady()) navigationRef.reset({ index: 0, routes: [{ name: 'Login' }] });
}

/**
 * Called when any call except login gets a 401, with the bearer token that call was sent with.
 * Parallel 401s share one run, so the token is cleared and the stack is reset once.
 */
export function handleUnauthorized(failedToken: string | null): Promise<void> {
  inFlight ??= endSession(failedToken).finally(() => {
    inFlight = null;
  });
  return inFlight;
}
