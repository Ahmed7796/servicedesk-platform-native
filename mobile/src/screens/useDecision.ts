import { useEffect, useRef, useState } from 'react';
import { approveServiceRequest, rejectServiceRequest } from '../api/generated/servicedesk';
import { decisionErrorMessage } from '../api/decisionErrors';

type Options = { onDone: () => void; reload: () => Promise<unknown> };

/** Sends one decision at a time. The server decides (403/409); the phone only reports what happened. */
export function useDecision(requestId: string, { onDone, reload }: Options) {
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false); // set synchronously, so two taps in one frame cannot both pass
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);

  async function send(call: () => Promise<{ data?: unknown; response?: Response }>) {
    if (inFlight.current) return;
    inFlight.current = true;
    setSubmitting(true);
    setError(null);
    let succeeded = false;
    try {
      const result = await call();
      if (result.data) {
        succeeded = true;
        onDone();
      } else {
        const status = result.response?.status;
        if (alive.current) setError(decisionErrorMessage(status));
        if (status === 409) await reload(); // someone else won: show the latest
      }
    } catch {
      if (alive.current) setError(decisionErrorMessage(500));
    } finally {
      inFlight.current = false;
      if (alive.current && !succeeded) setSubmitting(false);
    }
  }

  return {
    submitting,
    error,
    approve: (comment: string) => {
      const text = comment.trim();
      return send(() => approveServiceRequest({ path: { id: requestId }, body: text ? { comment: text } : {} }));
    },
    reject: (reason: string) => {
      const text = reason.trim();
      if (!text) return Promise.resolve();
      return send(() => rejectServiceRequest({ path: { id: requestId }, body: { reason: text } }));
    },
  };
}
