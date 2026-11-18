import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn().mockResolvedValue('jwt') }));
import { configureApi } from '../src/api/config';
import type { ApprovalStep, PendingItem, TriageSuggestion } from '../src/api/generated/servicedesk';
import { PendingApprovalsScreen } from '../src/screens/PendingApprovalsScreen';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const navigate = jest.fn();

// `satisfies` makes tsc check these against the generated contract types.
const step = {
  order: 1, position: 'DepartmentHead', scope: 'RequesterDepartment', decision: 'Pending',
  decidedById: null, decidedAt: null, comment: null, escalatedFrom: null,
} satisfies ApprovalStep;
const triage = {
  requestId: '11111111-1111-4111-8111-111111111111', category: 'SoftwareAccess', priority: 'High',
  suggestedRoute: ['DepartmentHead', 'ItManager'], summary: 'Needs a licence for the design tool.',
  confidence: 0.8, suggestedAt: '2026-11-17T10:00:00Z',
} satisfies TriageSuggestion;
const item = (id: string, over: Partial<PendingItem> = {}): PendingItem => ({
  ...({
    requestId: id, title: `Request ${id.slice(0, 4)}`, type: 'SoftwareAccess', status: 'InReview',
    requesterId: '22222222-2222-4222-8222-222222222222', requesterName: 'Rana Requester',
    departmentId: '33333333-3333-4333-8333-333333333333', createdAt: '2026-11-16T10:00:00Z',
    currentStep: step, triage: null,
  } satisfies PendingItem),
  ...over,
});
const A = '11111111-1111-4111-8111-111111111111';
const B = '44444444-4444-4444-8444-444444444444';

const ok = (body: unknown) => () =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
const status = (code: number) => () =>
  Promise.resolve(new Response(JSON.stringify({ title: 'x', status: code, stack: 'at secret.cs:1' }), {
    status: code, headers: { 'Content-Type': 'application/problem+json' },
  }));

async function renderScreen() {
  const navigation = { navigate } as never;
  await render(<PendingApprovalsScreen navigation={navigation} route={{ key: 'k', name: 'PendingApprovals' }} />);
}

beforeEach(() => {
  jest.clearAllMocks();
  fetchMock.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('PendingApprovalsScreen', () => {
  it('renders the pending items in the API order from pending-for-me', async () => {
    fetchMock.mockImplementation(ok([item(A), item(B, { title: 'Second one' })]));
    await renderScreen();

    expect(await screen.findByTestId(`pending-row-${A}`)).toBeTruthy();
    expect(screen.getByText('Request 1111')).toBeTruthy();
    expect(screen.getAllByText(/Rana Requester · waiting for Department head/)).toHaveLength(2);
    const rows = screen.getAllByTestId(/^pending-row-/).map((r) => r.props.testID);
    expect(rows).toEqual([`pending-row-${A}`, `pending-row-${B}`]);
    expect((fetchMock.mock.calls[0][0] as Request).url).toBe('http://proxy.test/api/service-requests/pending-for-me');
  });

  it('shows a loading state before the first response', async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    await renderScreen();
    expect(screen.getByTestId('pending-loading')).toBeTruthy();
  });

  it('shows the empty state for an empty list', async () => {
    fetchMock.mockImplementation(ok([]));
    await renderScreen();
    expect(await screen.findByTestId('pending-empty')).toBeTruthy();
    expect(screen.queryByTestId('pending-error')).toBeNull();
  });

  it('shows the suggestion as advisory text when triage exists, "No suggestion yet" when null', async () => {
    fetchMock.mockImplementation(ok([item(A, { triage }), item(B)]));
    await renderScreen();

    const summary = await screen.findByTestId('triage-summary');
    expect(summary.props.children).toEqual(['Suggested: ', 'Needs a licence for the design tool.']);
    expect(summary.props.numberOfLines).toBe(3);
    expect(screen.getByText('Suggested priority: High')).toBeTruthy();
    expect(screen.getAllByTestId('triage-none')).toHaveLength(1);
    expect(screen.getByText('No suggestion yet')).toBeTruthy();
    // Advisory only: the only pressables are the rows themselves.
    expect(screen.queryByText(/approve|reject/i)).toBeNull();
  });

  it('tapping a row navigates to RequestDetail with the request id', async () => {
    fetchMock.mockImplementation(ok([item(A)]));
    await renderScreen();
    await fireEvent.press(await screen.findByTestId(`pending-row-${A}`));
    expect(navigate).toHaveBeenCalledWith('RequestDetail', { requestId: A });
  });

  it('pull-to-refresh fetches the list again', async () => {
    fetchMock.mockImplementationOnce(ok([item(A)])).mockImplementationOnce(ok([item(A), item(B)]));
    await renderScreen();
    await screen.findByTestId(`pending-row-${A}`);

    await act(async () => {
      await screen.getByTestId('pending-list').props.refreshControl.props.onRefresh();
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(await screen.findByTestId(`pending-row-${B}`)).toBeTruthy();
  });

  it('an older, slower response cannot overwrite a newer one', async () => {
    let finishSlow!: (r: Response) => void;
    fetchMock
      .mockImplementationOnce(ok([item(A)]))
      .mockImplementationOnce(() => new Promise<Response>((r) => (finishSlow = r)))
      .mockImplementationOnce(ok([item(A), item(B)]));
    await renderScreen();
    await screen.findByTestId(`pending-row-${A}`);
    const refresh = () => screen.getByTestId('pending-list').props.refreshControl.props.onRefresh();

    await act(async () => {
      void refresh(); // second request: slow, left pending
    });
    await act(async () => {
      await refresh(); // third request: fast, newest
    });
    expect(await screen.findByTestId(`pending-row-${B}`)).toBeTruthy();

    await act(async () => finishSlow(await ok([item(A)])())); // the older request answers last
    expect(screen.getByTestId(`pending-row-${B}`)).toBeTruthy();
  });

  it('gives each row a spoken label with title, requester, position and suggestion', async () => {
    fetchMock.mockImplementation(ok([item(A, { triage }), item(B)]));
    await renderScreen();
    expect((await screen.findByTestId(`pending-row-${A}`)).props.accessibilityLabel).toBe(
      'Request 1111, requested by Rana Requester, waiting for Department head, has a suggestion',
    );
    expect(screen.getByTestId(`pending-row-${B}`).props.accessibilityLabel).toBe(
      'Request 4444, requested by Rana Requester, waiting for Department head',
    );
  });

  it('403 shows its message, with no raw body', async () => {
    fetchMock.mockImplementation(status(403));
    await renderScreen();
    const text = (await screen.findByTestId('pending-error')).props.children;
    expect(text).toMatch(/isn't allowed to see approvals/i);
    expect(text).not.toMatch(/secret|stack/);
    expect(screen.queryByTestId('pending-empty')).toBeNull(); // an error is not "nothing waiting"
  });

  it('a network error shows the unreachable message and keeps the list already on screen', async () => {
    fetchMock.mockImplementationOnce(ok([item(A)])).mockRejectedValueOnce(new TypeError('Network request failed'));
    await renderScreen();
    await screen.findByTestId(`pending-row-${A}`);

    await act(async () => {
      await screen.getByTestId('pending-list').props.refreshControl.props.onRefresh();
    });
    await waitFor(() => expect(screen.getByTestId('pending-error').props.children).toMatch(/can't reach the server/i));
    expect(screen.getByTestId(`pending-row-${A}`)).toBeTruthy();
  });
});
