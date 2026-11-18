// Plain-words messages for the pending list. 401 is handled by the session handler (M4-2b),
// which sends the user back to Login, so it gets the generic text here only as a fallback.

/** `status` is undefined when no HTTP response arrived (network down, proxy unreachable). */
export function pendingErrorMessage(status: number | undefined): string {
  if (status === undefined) return "Can't reach the server. Pull down to try again.";
  if (status === 403) return "Your account isn't allowed to see approvals.";
  return 'Something went wrong loading your approvals. Pull down to try again.';
}
