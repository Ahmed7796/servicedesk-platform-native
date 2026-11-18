const UNITS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
];

/**
 * "3 hours ago" where Intl.RelativeTimeFormat exists, otherwise an absolute date.
 * The check is at call time because Hermes may not ship RelativeTimeFormat.
 */
export function formatAge(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  if (typeof Intl !== 'undefined' && typeof Intl.RelativeTimeFormat === 'function') {
    const elapsed = Math.max(0, now.getTime() - date.getTime());
    const rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
    for (const [unit, ms] of UNITS) {
      if (elapsed >= ms) return rtf.format(-Math.floor(elapsed / ms), unit);
    }
    return rtf.format(0, 'minute');
  }
  return new Intl.DateTimeFormat('en', { year: 'numeric', month: 'short', day: 'numeric' }).format(date);
}
