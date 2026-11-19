import { requestIdFrom } from '../src/push/notificationData';

const ID = '11111111-1111-4111-8111-111111111111';
const wrap = (data: unknown) => ({ request: { identifier: 'n', content: { data } } });

describe('requestIdFrom', () => {
  it('returns a valid UUID, ignoring type and unknown fields', () => {
    expect(requestIdFrom(wrap({ requestId: ID, type: 'something.new', extra: 1 }))).toBe(ID);
    expect(requestIdFrom(wrap({ requestId: ID }))).toBe(ID);
  });
  it('accepts upper case', () => {
    expect(requestIdFrom(wrap({ requestId: ID.toUpperCase() }))).toBe(ID.toUpperCase());
  });
  it.each([
    ['missing', {}],
    ['empty', { requestId: '' }],
    ['not a uuid', { requestId: 'abc' }],
    ['uuid with a suffix', { requestId: `${ID}x` }],
    ['uuid with a prefix', { requestId: ` ${ID}` }],
    ['a number', { requestId: 5 }],
    ['an object', { requestId: { id: ID } }],
    ['null', { requestId: null }],
  ])('returns null when requestId is %s', (_name, data) => {
    expect(requestIdFrom(wrap(data))).toBeNull();
  });
  it.each([null, undefined, 'text', 5, {}, { request: null }, { request: { content: null } }, wrap(null), wrap('x')])(
    'returns null for a malformed notification %p',
    (input) => {
      expect(requestIdFrom(input)).toBeNull();
    },
  );
});
