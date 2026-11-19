import { useEffect } from 'react';
import * as Notifications from 'expo-notifications';
import { requestIdFrom } from './notificationData';
import { openRequest, refreshPendingList } from './openRequest';
import { navigationRef } from '../auth/session';

// Foreground: show the banner and the list entry; no sound, no badge.
Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: false,
    shouldSetBadge: false,
  }),
});

// A cold start reports the same last response again on every mount; open each notification once.
// A notification counts as handled only when the open actually proceeds, so a tap that arrives
// before the navigator is ready is not lost.
const handled = new Set<string>();

type Waiters = Set<() => void>;

function handleResponse(response: Notifications.NotificationResponse | null, waiters: Waiters): void {
  if (!response) return;
  const id = response.notification.request.identifier;
  if (handled.has(id)) return;
  const requestId = requestIdFrom(response.notification);
  if (!requestId) {
    handled.add(id); // nothing to open, now or later
    return;
  }
  if (!navigationRef.isReady()) {
    // Try again at the navigator's next state event, once.
    const stop = navigationRef.addListener('state', () => {
      stop();
      waiters.delete(stop);
      handleResponse(response, waiters);
    });
    waiters.add(stop);
    return;
  }
  handled.add(id);
  void openRequest(requestId);
}

/** Listens for pushes once the navigator exists (`ready`). Removes both listeners on unmount. */
export function useNotifications(ready: boolean): void {
  useEffect(() => {
    if (!ready) return;
    const waiters: Waiters = new Set();
    const received = Notifications.addNotificationReceivedListener((notification) => {
      if (requestIdFrom(notification)) refreshPendingList();
    });
    const responded = Notifications.addNotificationResponseReceivedListener((r) => handleResponse(r, waiters));
    handleResponse(Notifications.getLastNotificationResponse(), waiters); // opened from a notification
    return () => {
      received.remove();
      responded.remove();
      waiters.forEach((stop) => stop());
    };
  }, [ready]);
}
