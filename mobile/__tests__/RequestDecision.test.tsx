import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn().mockResolvedValue('jwt') }));
import { configureApi } from '../src/api/config';
import type {
  ApproveRequest, ApprovalStep, RejectRequest, RequestStatus, ServiceRequest, ServiceRequestDetail,
} from '../src/api/generated/servicedesk';
import { RequestDetailScreen } from '../src/screens/RequestDetailScreen';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const popTo = jest.fn();
const ID = '11111111-1111-4111-8111-111111111111';
const USER = '22222222-2222-4222-8222-222222222222';

// `satisfies` makes tsc check these against the generated contract types.
const step = (order: number, over: Partial<ApprovalStep> = {}): ApprovalStep => ({
  ...({
    order, position: 'DepartmentHead', scope: 'RequesterDepartment', decision: 'Pending',
    decidedById: null, decidedAt: null, comment: null, escalatedFrom: null,
  } satisfies ApprovalStep),
  ...over,
});
const detail = (over: Partial<ServiceRequestDetail> = {}): ServiceRequestDetail => ({
  ...({
    id: ID, title: 'Design tool licence', description: 'Need Figma.', type: 'SoftwareAccess', status: 'InReview',
    requesterId: USER, departmentId: '33333333-3333-4333-8333-333333333333',
    createdAt: '2026-11-16T10:00:00Z', updatedAt: '2026-11-16T11:00:00Z',
    steps: [step(1, { decision: 'Approved' }), step(2)],
    requesterName: 'Rana Requester', auditTrail: [], triage: null,
  } satisfies ServiceRequestDetail),
  ...over,
});
const plain = (d: ServiceRequestDetail): ServiceRequest => {
  const { requesterName: _n, auditTrail: _a, triage: _t, ...rest } = d;
  return rest satisfies ServiceRequest;
};

const reply = (body: unknown, code = 200) => () =>
  Promise.resolve(new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } }));
const emptyReply = (code: number) => () => Promise.resolve(new Response(null, { status: code }));
const networkDown = () => Promise.reject(new TypeError('Network request failed'));

type Responder = () => Promise<Response>;
/** GET detail and POST decision answered separately. `get` is a queue; the last responder repeats. */
function serve(get: Responder[], post: Responder) {
  fetchMock.mockImplementation((request: Request) => {
    if (request.method === 'POST') return post();
    return (get.length > 1 ? get.shift()! : get[0])();
  });
}
const posts = () => fetchMock.mock.calls.map((c) => c[0] as Request).filter((r) => r.method === 'POST');
const gets = () => fetchMock.mock.calls.map((c) => c[0] as Request).filter((r) => r.method === 'GET');

async function renderScreen() {
  const navigation = { popTo } as never;
  await render(<RequestDetailScreen navigation={navigation} route={{ key: 'k', name: 'RequestDetail', params: { requestId: ID } }} />);
  await screen.findByText('Design tool licence');
}
const type = (text: string) => fireEvent.changeText(screen.getByTestId('decision-input'), text);
const press = (id: 'approve-button' | 'reject-button') => fireEvent.press(screen.getByTestId(id));
const isDisabled = (id: string) => Boolean(screen.getByTestId(id).props.accessibilityState?.disabled);

beforeEach(() => {
  jest.clearAllMocks();
  fetchMock.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('Approve and Reject: visibility', () => {
  // Contract RequestStatus: 'Submitted' | 'InReview' | 'Approved' | 'Rejected' | 'Fulfilled' | 'Cancelled'.
  it.each(['Submitted', 'InReview'] satisfies RequestStatus[])('shows both buttons for %s with a pending step', async (status) => {
    serve([reply(detail({ status }))], reply({}));
    await renderScreen();
    expect(screen.getByTestId('approve-button')).toBeTruthy();
    expect(screen.getByTestId('reject-button')).toBeTruthy();
  });

  it.each(['Approved', 'Rejected', 'Fulfilled', 'Cancelled'] satisfies RequestStatus[])('hides both buttons for %s', async (status) => {
    serve([reply(detail({ status }))], reply({}));
    await renderScreen();
    expect(screen.queryByTestId('approve-button')).toBeNull();
    expect(screen.queryByTestId('reject-button')).toBeNull();
    expect(screen.queryByTestId('decision-input')).toBeNull();
  });

  it('hides both buttons when no step is pending', async () => {
    serve([reply(detail({ steps: [step(1, { decision: 'Approved' }), step(2, { decision: 'Approved' })] }))], reply({}));
    await renderScreen();
    expect(screen.queryByTestId('approve-button')).toBeNull();
    expect(screen.queryByTestId('reject-button')).toBeNull();
  });
});

describe('Approve', () => {
  it('sends the typed, trimmed comment and then goes back to the list with a refresh param', async () => {
    serve([reply(detail())], reply(plain(detail({ status: 'Approved' }))));
    await renderScreen();
    await type('  Looks fine.  ');
    await press('approve-button');

    await waitFor(() => expect(popTo).toHaveBeenCalledTimes(1));
    const [request] = posts();
    expect(request.url).toBe(`http://proxy.test/api/service-requests/${ID}/approve`);
    expect(request.headers.get('Authorization')).toBe('Bearer jwt');
    expect(await request.clone().json()).toEqual({ comment: 'Looks fine.' } satisfies ApproveRequest);
    expect(popTo).toHaveBeenCalledWith('PendingApprovals', { refresh: expect.any(Number) });
  });

  it.each([[''], ['   ']])('sends an empty body when the comment is %j', async (text) => {
    serve([reply(detail())], reply(plain(detail())));
    await renderScreen();
    await type(text);
    await press('approve-button');

    await waitFor(() => expect(popTo).toHaveBeenCalledTimes(1));
    expect(await posts()[0].clone().json()).toEqual({} satisfies ApproveRequest);
  });
});

describe('Reject', () => {
  it('is disabled while the reason is blank, and sends nothing if pressed', async () => {
    serve([reply(detail())], reply(plain(detail())));
    await renderScreen();
    expect(isDisabled('reject-button')).toBe(true);
    await press('reject-button');
    await type('   ');
    expect(isDisabled('reject-button')).toBe(true);
    await press('reject-button');
    expect(posts()).toHaveLength(0);
    expect(isDisabled('approve-button')).toBe(false); // Approve does not need text
  });

  it('sends the trimmed reason and navigates with a refresh param', async () => {
    serve([reply(detail())], reply(plain(detail({ status: 'Rejected' }))));
    await renderScreen();
    await type('  Over budget.  ');
    expect(isDisabled('reject-button')).toBe(false);
    await press('reject-button');

    await waitFor(() => expect(popTo).toHaveBeenCalledTimes(1));
    const [request] = posts();
    expect(request.url).toBe(`http://proxy.test/api/service-requests/${ID}/reject`);
    expect(await request.clone().json()).toEqual({ reason: 'Over budget.' } satisfies RejectRequest);
    expect(popTo).toHaveBeenCalledWith('PendingApprovals', { refresh: expect.any(Number) });
  });
});

describe('double submit', () => {
  it('two quick taps send one request, and both buttons are disabled meanwhile', async () => {
    let finish: (r: Response) => void = () => {};
    serve([reply(detail())], () => new Promise<Response>((resolve) => { finish = resolve; }));
    await renderScreen();
    await type('ok');
    // All three presses land inside one act(), before React re-renders, so only the in-flight ref can stop the extras.
    const approve = screen.getByTestId('approve-button');
    const reject = screen.getByTestId('reject-button');
    await act(async () => {
      fireEvent.press(approve);
      fireEvent.press(approve);
      fireEvent.press(reject);
    });

    await waitFor(() => expect(isDisabled('approve-button')).toBe(true));
    expect(isDisabled('reject-button')).toBe(true);
    expect(posts()).toHaveLength(1);

    finish(new Response(JSON.stringify(plain(detail())), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    await waitFor(() => expect(popTo).toHaveBeenCalledTimes(1));
    expect(posts()).toHaveLength(1);
  });
});

describe('failures', () => {
  const MESSAGES = {
    403: "You can't decide this step. It may be waiting on a different approver.",
    409: 'Someone else has acted on this request, or it changed. Showing the latest.',
    400: "That wasn't valid. Check what you typed and try again.",
    404: 'This request no longer exists.',
    500: 'Something went wrong sending your decision. Try again.',
  } as const;

  it.each([[403], [409], [400], [404], [500]] as const)('%s shows its message, does not navigate and re-enables the buttons', async (code) => {
    serve([reply(detail())], reply({ title: 'x', stack: 'at secret.cs:1' }, code));
    await renderScreen();
    await type('ok');
    await press('approve-button');

    expect((await screen.findByTestId('decision-error')).props.children).toBe(MESSAGES[code]);
    expect(screen.queryByText(/secret/)).toBeNull();
    expect(popTo).not.toHaveBeenCalled();
    await waitFor(() => expect(isDisabled('approve-button')).toBe(false));
    expect(isDisabled('reject-button')).toBe(false);
    expect(gets()).toHaveLength(code === 409 ? 2 : 1);
  });

  it('403 with an empty body relies on the status code only', async () => {
    serve([reply(detail())], emptyReply(403));
    await renderScreen();
    await press('approve-button');
    expect((await screen.findByTestId('decision-error')).props.children).toBe(MESSAGES[403]);
  });

  it('a network failure says the decision was not sent', async () => {
    serve([reply(detail())], networkDown);
    await renderScreen();
    await type('nope');
    await press('reject-button');
    expect((await screen.findByTestId('decision-error')).props.children).toBe(
      "Can't reach the server. Your decision was not sent. Try again.",
    );
    expect(popTo).not.toHaveBeenCalled();
    await waitFor(() => expect(isDisabled('reject-button')).toBe(false));
  });

  it('401 shows no decision text and does not navigate (the interceptor owns it)', async () => {
    serve([reply(detail())], reply({}, 401));
    await renderScreen();
    await press('approve-button');
    await waitFor(() => expect(isDisabled('approve-button')).toBe(false));
    expect(screen.queryByTestId('decision-error')).toBeNull();
    expect(popTo).not.toHaveBeenCalled();
  });

  it('after a failure, a second try sends a new request', async () => {
    serve([reply(detail())], reply({}, 500));
    await renderScreen();
    await press('approve-button');
    await screen.findByTestId('decision-error');
    await press('approve-button');
    await waitFor(() => expect(posts()).toHaveLength(2));
  });

  it('409 refetches the detail: a request that is no longer open loses its buttons but keeps the message', async () => {
    serve([reply(detail()), reply(detail({ status: 'Approved', steps: [step(1, { decision: 'Approved' }), step(2, { decision: 'Approved' })] }))], reply({}, 409));
    await renderScreen();
    await press('approve-button');

    expect((await screen.findByTestId('decision-error')).props.children).toBe(MESSAGES[409]);
    await waitFor(() => expect(screen.queryByTestId('approve-button')).toBeNull());
    expect(screen.getByTestId('decision-error')).toBeTruthy();
    expect(screen.getByTestId('detail-status').props.children).toEqual(['Status: ', 'Approved']);
  });

  it('409 followed by a failing refetch: the detail error shows too, and the old detail stays', async () => {
    serve([reply(detail()), reply({}, 500)], reply({}, 409));
    await renderScreen();
    await press('approve-button');

    expect((await screen.findByTestId('detail-error')).props.children).toBe('Something went wrong loading this request.');
    expect(screen.getByTestId('decision-error').props.children).toBe(MESSAGES[409]);
    expect(screen.getByText('Design tool licence')).toBeTruthy();
    expect(popTo).not.toHaveBeenCalled();
  });
});
