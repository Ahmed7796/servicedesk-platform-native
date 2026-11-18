import { useCallback, useEffect, useRef, useState } from 'react';
import { listPendingForMe } from '../api/generated/servicedesk';
import type { PendingItem } from '../api/generated/servicedesk';
import { listOverdue } from '../api/generated/notification';
import type { OverdueStep } from '../api/generated/notification';
import { pendingErrorMessage } from '../api/pendingErrors';

/**
 * Loads `pending-for-me` and `/notify/overdue` in parallel. On a failed refresh the previous list
 * stays on screen. The overdue call is best effort: any failure (404, network, 5xx) means no
 * badges, never an error, and the list does not wait for it.
 */
export function usePendingApprovals() {
  const [items, setItems] = useState<PendingItem[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [overdue, setOverdue] = useState<OverdueStep[]>([]);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  const latest = useRef(0); // request counter: only the newest request may update state

  const load = useCallback(async () => {
    const mine = ++latest.current;
    const current = () => alive.current && mine === latest.current;
    void listOverdue()
      .then((r) => r.data ?? [], () => [])
      .then((steps) => {
        if (current()) setOverdue(steps);
      });
    try {
      const result = await listPendingForMe();
      if (!current()) return;
      if (result.data) {
        setItems(result.data);
        setError(null);
      } else {
        setError(pendingErrorMessage(result.response?.status));
      }
    } catch {
      if (current()) setError(pendingErrorMessage(500));
    }
    if (current()) {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    alive.current = true;
    void load();
    return () => {
      alive.current = false;
    };
  }, [load]);

  const refresh = useCallback(() => {
    setRefreshing(true);
    return load();
  }, [load]);

  return { items, overdue, loading, refreshing, error, refresh };
}
