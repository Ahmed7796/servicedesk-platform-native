import { useCallback, useEffect, useRef, useState } from 'react';
import { getServiceRequest } from '../api/generated/servicedesk';
import type { ServiceRequestDetail } from '../api/generated/servicedesk';
import { detailErrorMessage } from '../api/detailErrors';

/** Loads one request. Only the newest call may update state; a failed reload keeps what is on screen. */
export function useRequestDetail(requestId: string) {
  const [detail, setDetail] = useState<ServiceRequestDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);
  const latest = useRef(0);

  const load = useCallback(async () => {
    const mine = ++latest.current;
    const current = () => alive.current && mine === latest.current;
    setLoading(true);
    try {
      const result = await getServiceRequest({ path: { id: requestId } });
      if (!current()) return;
      if (result.data) {
        setDetail(result.data);
        setError(null);
      } else if (result.response?.status === 401) {
        return; // The API interceptor ends the session and resets to Login; show nothing in the meantime.
      } else {
        setError(detailErrorMessage(result.response?.status));
      }
    } catch {
      if (current()) setError(detailErrorMessage(undefined));
    }
    if (current()) setLoading(false);
  }, [requestId]);

  useEffect(() => {
    alive.current = true;
    setDetail(null); // a previous request must never show under a new id
    setError(null);
    void load();
    return () => {
      alive.current = false;
    };
  }, [load]);

  return { detail, loading, error, reload: load };
}
