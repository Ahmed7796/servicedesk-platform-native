import { pendingErrorMessage } from '../src/api/pendingErrors';
import { formatAge } from '../src/api/formatAge';

describe('pendingErrorMessage', () => {
  it('maps network, 403 and everything else to plain sentences', () => {
    expect(pendingErrorMessage(undefined)).toMatch(/can't reach the server/i);
    expect(pendingErrorMessage(403)).toMatch(/isn't allowed/i);
    expect(pendingErrorMessage(500)).toMatch(/something went wrong/i);
  });
});

describe('formatAge', () => {
  const now = new Date('2026-11-18T12:00:00Z');
  it('is relative where Intl.RelativeTimeFormat exists', () => {
    expect(formatAge('2026-11-18T09:00:00Z', now)).toBe('3 hours ago');
    expect(formatAge('2026-11-16T12:00:00Z', now)).toBe('2 days ago');
  });
  it('falls back to an absolute date without RelativeTimeFormat', () => {
    const original = Intl.RelativeTimeFormat;
    (Intl as { RelativeTimeFormat?: unknown }).RelativeTimeFormat = undefined;
    try {
      expect(formatAge('2026-11-16T12:00:00Z', now)).toBe('Nov 16, 2026');
    } finally {
      (Intl as { RelativeTimeFormat?: unknown }).RelativeTimeFormat = original;
    }
  });
  it('returns an empty string for an unreadable date', () => {
    expect(formatAge('not a date', now)).toBe('');
  });
});
