import { decisionErrorMessage } from '../src/api/decisionErrors';

describe('decisionErrorMessage', () => {
  it.each([
    [403, "You can't decide this step. It may be waiting on a different approver."],
    [409, 'Someone else has acted on this request, or it changed. Showing the latest.'],
    [400, "That wasn't valid. Check what you typed and try again."],
    [404, 'This request no longer exists.'],
    [undefined, "Can't reach the server. Your decision was not sent. Try again."],
    [500, 'Something went wrong sending your decision. Try again.'],
    [502, 'Something went wrong sending your decision. Try again.'],
  ])('maps %s', (status, message) => {
    expect(decisionErrorMessage(status)).toBe(message);
  });

  it('returns null for 401: the API interceptor owns it', () => {
    expect(decisionErrorMessage(401)).toBeNull();
  });
});
