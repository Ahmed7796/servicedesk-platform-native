// Plain-words messages for approving or rejecting. 401 returns null: the API interceptor (M4-2b) ends the session.

/** `status` is undefined when no HTTP response arrived. The 403 text names no reason: the body may be empty. */
export function decisionErrorMessage(status: number | undefined): string | null {
  if (status === 401) return null;
  if (status === undefined) return "Can't reach the server. Your decision was not sent. Try again.";
  if (status === 403) return "You can't decide this step. It may be waiting on a different approver.";
  if (status === 409) return 'Someone else has acted on this request, or it changed. Showing the latest.';
  if (status === 400) return "That wasn't valid. Check what you typed and try again.";
  if (status === 404) return 'This request no longer exists.';
  return 'Something went wrong sending your decision. Try again.';
}
