import { useCallback, useEffect, useRef, useState } from 'react';
import { listPendingForMe } from '../api/generated/servicedesk';
import type { PendingItem } from '../api/generated/servicedesk';
import { pendingErrorMessage } from '../api/pendingErrors';

/** Loads `pending-for-me`. On a failed refresh the previous list stays on screen. */
export function usePendingApprovals() {
  const [items, setItems] = useState<PendingItem[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  const latest = useRef(0); // request counter: only the newest request may update state

  const load = useCallback(async () => {
    const mine = ++latest.current;
    const current = () => alive.current && mine === latest.current;
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

  return { items, loading, refreshing, error, refresh };
}
