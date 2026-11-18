// Plain-words messages for loading one request. 401 is handled by the session handler (M4-2b).

/** `status` is undefined when no HTTP response arrived (network down, proxy unreachable). */
export function detailErrorMessage(status: number | undefined): string {
  if (status === undefined) return "Can't reach the server.";
  if (status === 404) return 'This request no longer exists.';
  return 'Something went wrong loading this request.';
}
