// Turns a failed login call into one plain sentence. Never returns raw JSON or a stack trace.

const WRONG_CREDENTIALS = 'Wrong email, employee number or password.';
const VALIDATION_FALLBACK = 'Please enter your email or employee number and your password.';
const UNREACHABLE = "Can't reach the server. Check your connection and try again.";
const GENERIC = 'Something went wrong. Please try again.';

const MAX_MESSAGE_LENGTH = 200;

// Long or empty server text is not shown; the caller falls back to a fixed sentence.
function asText(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const text = value.trim();
  return text && text.length <= MAX_MESSAGE_LENGTH ? text : null;
}

// 400 bodies are ValidationProblemDetails: field messages in `errors`, else `detail`, else a fixed sentence (also when the text is over 200 characters).
function validationMessage(body: unknown): string {
  if (typeof body !== 'object' || body === null) return VALIDATION_FALLBACK;
  const { errors, detail } = body as { errors?: unknown; detail?: unknown };
  if (typeof errors === 'object' && errors !== null) {
    for (const messages of Object.values(errors)) {
      if (Array.isArray(messages)) {
        const first = messages.map(asText).find((m) => m !== null);
        if (first) return first;
      }
    }
  }
  return asText(detail) ?? VALIDATION_FALLBACK;
}

/** `status` is undefined when no HTTP response arrived (network down, proxy unreachable). */
export function loginErrorMessage(status: number | undefined, body?: unknown): string {
  if (status === undefined) return UNREACHABLE;
  if (status === 401) return WRONG_CREDENTIALS;
  if (status === 400) return validationMessage(body);
  return GENERIC;
}
