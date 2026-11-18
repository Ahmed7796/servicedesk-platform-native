import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn().mockResolvedValue('jwt') }));
import { configureApi } from '../src/api/config';
import type { ApprovalStep, PendingItem, TriageSuggestion } from '../src/api/generated/servicedesk';
import type { OverdueStep } from '../src/api/generated/notification';
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

const overdueStep = (requestId: string, stepOrder: number): OverdueStep => ({
  requestId, title: 'x', requesterId: '22222222-2222-4222-8222-222222222222', stepOrder,
  position: 'DepartmentHead', assignedUserIds: [], pendingSince: '2026-11-15T10:00:00Z', dueAt: '2026-11-16T10:00:00Z',
} satisfies OverdueStep);

type Responder = () => Promise<Response>;
const urls = () => fetchMock.mock.calls.map((c) => (c[0] as Request).url);
const count = (part: string) => urls().filter((u) => u.includes(part)).length;
/** Answers by URL. Each list is a queue; the last responder repeats. Overdue defaults to an empty list. */
function serve(pending: Responder[], overdue: Responder[] = [ok([])]) {
  fetchMock.mockImplementation((request: Request) => {
    const queue = request.url.includes('/notify/overdue') ? overdue : pending;
    return (queue.length > 1 ? queue.shift()! : queue[0])();
  });
}

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
    serve([ok([item(A), item(B, { title: 'Second one' })])]);
    await renderScreen();

    expect(await screen.findByTestId(`pending-row-${A}`)).toBeTruthy();
    expect(screen.getByText('Request 1111')).toBeTruthy();
    expect(screen.getAllByText(/Rana Requester · waiting for Department head/)).toHaveLength(2);
    const rows = screen.getAllByTestId(/^pending-row-/).map((r) => r.props.testID);
    expect(rows).toEqual([`pending-row-${A}`, `pending-row-${B}`]);
    expect(urls()).toContain('http://proxy.test/api/service-requests/pending-for-me');
  });

  it('shows a loading state before the first response', async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    await renderScreen();
    expect(screen.getByTestId('pending-loading')).toBeTruthy();
  });

  it('shows the empty state for an empty list', async () => {
    serve([ok([])]);
    await renderScreen();
    expect(await screen.findByTestId('pending-empty')).toBeTruthy();
    expect(screen.queryByTestId('pending-error')).toBeNull();
  });

  it('shows the suggestion as advisory text when triage exists, "No suggestion yet" when null', async () => {
    serve([ok([item(A, { triage }), item(B)])]);
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
    serve([ok([item(A)])]);
    await renderScreen();
    await fireEvent.press(await screen.findByTestId(`pending-row-${A}`));
    expect(navigate).toHaveBeenCalledWith('RequestDetail', { requestId: A });
  });

  it('pull-to-refresh fetches the list again', async () => {
    serve([ok([item(A)]), ok([item(A), item(B)])]);
    await renderScreen();
    await screen.findByTestId(`pending-row-${A}`);

    await act(async () => {
      await screen.getByTestId('pending-list').props.refreshControl.props.onRefresh();
    });
    expect(count('pending-for-me')).toBe(2);
    expect(count('/notify/overdue')).toBe(2); // refresh hits both endpoints
    expect(await screen.findByTestId(`pending-row-${B}`)).toBeTruthy();
  });

  it('an older, slower response cannot overwrite a newer one', async () => {
    let finishSlow!: (r: Response) => void;
    serve([
      ok([item(A)]),
      () => new Promise<Response>((r) => (finishSlow = r)),
      ok([item(A), item(B)]),
    ]);
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
    serve([ok([item(A, { triage }), item(B)])]);
    await renderScreen();
    expect((await screen.findByTestId(`pending-row-${A}`)).props.accessibilityLabel).toBe(
      'Request 1111, requested by Rana Requester, waiting for Department head, has a suggestion',
    );
    expect(screen.getByTestId(`pending-row-${B}`).props.accessibilityLabel).toBe(
      'Request 4444, requested by Rana Requester, waiting for Department head',
    );
  });

  it('403 shows its message, with no raw body', async () => {
    serve([status(403)]);
    await renderScreen();
    const text = (await screen.findByTestId('pending-error')).props.children;
    expect(text).toMatch(/isn't allowed to see approvals/i);
    expect(text).not.toMatch(/secret|stack/);
    expect(screen.queryByTestId('pending-empty')).toBeNull(); // an error is not "nothing waiting"
  });

  it('a network error shows the unreachable message and keeps the list already on screen', async () => {
    serve([ok([item(A)]), () => Promise.reject(new TypeError('Network request failed'))]);
    await renderScreen();
    await screen.findByTestId(`pending-row-${A}`);

    await act(async () => {
      await screen.getByTestId('pending-list').props.refreshControl.props.onRefresh();
    });
    await waitFor(() => expect(screen.getByTestId('pending-error').props.children).toMatch(/can't reach the server/i));
    expect(screen.getByTestId(`pending-row-${A}`)).toBeTruthy();
  });

  describe('overdue badge', () => {
    it('shows the badge when requestId and step order match, and adds "overdue" to the spoken label', async () => {
      serve([ok([item(A), item(B)])], [ok([overdueStep(A, 1)])]);
      await renderScreen();

      const row = await screen.findByTestId(`pending-row-${A}`);
      await waitFor(() => expect(screen.getAllByTestId('overdue-badge')).toHaveLength(1));
      expect(screen.getByTestId('overdue-badge').props.accessibilityLabel).toBe('Overdue');
      expect(row.props.accessibilityLabel).toMatch(/, overdue$/);
      expect(screen.getByTestId(`pending-row-${B}`).props.accessibilityLabel).not.toMatch(/overdue/);
    });

    it('shows no badge for the same request at a different step', async () => {
      serve([ok([item(A)])], [ok([overdueStep(A, 2)])]);
      await renderScreen();
      await screen.findByTestId(`pending-row-${A}`);
      await act(async () => {});
      expect(count('/notify/overdue')).toBe(1);
      expect(screen.queryByTestId('overdue-badge')).toBeNull();
    });

    it.each([
      ['404', status(404)],
      ['a network error', () => Promise.reject(new TypeError('Network request failed'))],
      ['a 500', status(500)],
    ])('still shows the list, with no badge and no banner, when /notify/overdue fails with %s', async (_n, failing) => {
      // The failure arrives after the list, so a banner set by it would not be cleared by the list load.
      serve([ok([item(A)])], [() => new Promise<Response>((r) => setTimeout(() => r(failing()), 20))]);
      await renderScreen();
      expect(await screen.findByTestId(`pending-row-${A}`)).toBeTruthy();
      await act(async () => {
        await new Promise((r) => setTimeout(r, 60));
      });
      expect(count('/notify/overdue')).toBe(1);
      expect(screen.queryByTestId('overdue-badge')).toBeNull();
      expect(screen.queryByTestId('pending-error')).toBeNull();
    });

    it('an older overdue response arriving after a newer load cannot overwrite its badges', async () => {
      let finishOld!: (r: Response) => void;
      serve([ok([item(A)])], [() => new Promise<Response>((r) => (finishOld = r)), ok([overdueStep(A, 1)])]);
      await renderScreen();
      await screen.findByTestId(`pending-row-${A}`);

      await act(async () => {
        await screen.getByTestId('pending-list').props.refreshControl.props.onRefresh(); // newer load: badge for A
      });
      await waitFor(() => expect(screen.getByTestId('overdue-badge')).toBeTruthy());

      await act(async () => finishOld(await ok([])())); // the older overdue answer (no badges) arrives last
      expect(screen.getByTestId('overdue-badge')).toBeTruthy();
    });

    it('does not wait for a slow /notify/overdue before showing the list', async () => {
      serve([ok([item(A)])], [() => new Promise<Response>(() => {})]);
      await renderScreen();
      expect(await screen.findByTestId(`pending-row-${A}`)).toBeTruthy();
    });
  });
});
