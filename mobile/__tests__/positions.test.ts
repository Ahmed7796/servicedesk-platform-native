import type { ApproverPosition } from '../src/api/generated/servicedesk';
import { positionLabel } from '../src/api/positions';

// Compile-time check, run by tsc (npm test runs `tsc --noEmit` first): the generated
// type must be exactly string, not a closed union. If it changes, this line stops compiling.
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
export const positionIsString: Equal<ApproverPosition, string> = true;

describe('positionLabel', () => {
  it('labels known positions', () => {
    expect(positionLabel('ItManager')).toBe('IT manager');
  });
  it('falls back to the raw string for unknown ones', () => {
    expect(positionLabel('RegionalDirector')).toBe('RegionalDirector');
    expect(positionLabel('toString')).toBe('toString');
  });
});
