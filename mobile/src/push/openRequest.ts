import { CommonActions, StackActions } from '@react-navigation/native';
import { navigationRef } from '../auth/session';
import { tokenStore } from '../auth/tokenStore';

/**
 * A tap on a push: open that request, always with exactly one RequestDetail entry. If one is open,
 * `popTo` reuses it and replaces its id (a plain push would stack a second one; and `popTo` on a
 * stack without one would replace PendingApprovals, so it is not used then). Signed out (no token, or the keystore
 * cannot be read): go to Login and drop the link. Does nothing before the navigator is ready.
 */
export async function openRequest(requestId: string): Promise<void> {
  if (!navigationRef.isReady()) return;
  const token = await tokenStore.get().catch(() => null);
  if (!navigationRef.isReady()) return;
  if (!token) {
    navigationRef.dispatch(CommonActions.reset({ index: 0, routes: [{ name: 'Login' }] }));
    return;
  }
  const names = (navigationRef.getRootState()?.routes ?? []).map((route) => route.name);
  if (!names.includes('PendingApprovals')) {
    // Signed in but still showing Login (the app was not restarted): build the normal stack.
    navigationRef.dispatch(
      CommonActions.reset({ index: 1, routes: [{ name: 'PendingApprovals' }, { name: 'RequestDetail', params: { requestId } }] }),
    );
  } else if (names.includes('RequestDetail')) {
    navigationRef.dispatch(StackActions.popTo('RequestDetail', { requestId })); // reuse the open entry
  } else {
    navigationRef.dispatch(StackActions.push('RequestDetail', { requestId }));
  }
}

/** A push arrived while the app is open: refresh the list if that is what the user is looking at. */
export function refreshPendingList(): void {
  if (navigationRef.isReady() && navigationRef.getCurrentRoute()?.name === 'PendingApprovals') {
    navigationRef.dispatch(CommonActions.setParams({ refresh: Date.now() }));
  }
}
