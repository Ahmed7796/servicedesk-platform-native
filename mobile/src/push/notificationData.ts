import type { PushData } from '../api/generated/notification';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The request id carried by a push (contract `PushData`), or null. Only `requestId` is used;
 * `type` and unknown fields are ignored. The input is untrusted, so it is checked as unknown.
 * Accepts a Notification (foreground) or the `.notification` of a response (tap).
 */
export function requestIdFrom(notification: unknown): PushData['requestId'] | null {
  const data = (notification as { request?: { content?: { data?: unknown } } } | null | undefined)?.request?.content
    ?.data;
  if (typeof data !== 'object' || data === null) return null;
  const id = (data as Partial<PushData>).requestId;
  return typeof id === 'string' && UUID.test(id) ? id : null;
}
