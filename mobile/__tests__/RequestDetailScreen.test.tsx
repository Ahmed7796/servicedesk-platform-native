import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react-native';

jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn().mockResolvedValue('jwt') }));
import { configureApi } from '../src/api/config';
import type { ApprovalStep, AuditEntry, ServiceRequestDetail, TriageSuggestion } from '../src/api/generated/servicedesk';
import { detailErrorMessage } from '../src/api/detailErrors';
import { RequestDetailScreen } from '../src/screens/RequestDetailScreen';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const ID = '11111111-1111-4111-8111-111111111111';
const USER = '22222222-2222-4222-8222-222222222222';

// `satisfies` makes tsc check these against the generated contract types.
const step = (order: number, position: string, over: Partial<ApprovalStep> = {}): ApprovalStep => ({
  ...({
    order, position, scope: 'RequesterDepartment', decision: 'Pending',
    decidedById: null, decidedAt: null, comment: null, escalatedFrom: null,
  } satisfies ApprovalStep),
  ...over,
});
const triage = {
  requestId: ID, category: 'SoftwareAccess', priority: 'High', suggestedRoute: ['DepartmentHead', 'ItManager'],
  summary: 'Needs a licence for the design tool.', confidence: 0.8, suggestedAt: '2026-11-17T10:00:00Z',
} satisfies TriageSuggestion;
const audit = (action: AuditEntry['action'], actorName: string, occurredAt: string, over: Partial<AuditEntry> = {}): AuditEntry => ({
  ...({
    action, statusBefore: 'Submitted', statusAfter: 'InReview', actorId: USER, actorName, details: null, occurredAt,
  } satisfies AuditEntry),
  ...over,
});
const detail = (over: Partial<ServiceRequestDetail> = {}): ServiceRequestDetail => ({
  ...({
    id: ID, title: 'Design tool licence', description: 'Need Figma.', type: 'SoftwareAccess', status: 'InReview',
    requesterId: USER, departmentId: '33333333-3333-4333-8333-333333333333',
    createdAt: '2026-11-16T10:00:00Z', updatedAt: '2026-11-16T11:00:00Z',
    steps: [step(1, 'DepartmentHead', { decision: 'Approved', comment: 'fine' }), step(2, 'ItManager')],
    requesterName: 'Rana Requester', auditTrail: [], triage: null,
  } satisfies ServiceRequestDetail),
  ...over,
});

const json = (body: unknown, code = 200) => () =>
  Promise.resolve(new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } }));
const urls = () => fetchMock.mock.calls.map((c) => (c[0] as Request).url);

const element = (requestId: string) => (
  <RequestDetailScreen navigation={{} as never} route={{ key: 'k', name: 'RequestDetail', params: { requestId } }} />
);
async function renderScreen() {
  return render(element(ID));
}

beforeEach(() => {
  fetchMock.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('RequestDetailScreen (read-only)', () => {
  it('loads the request through the API and shows title, requester, status and steps', async () => {
    fetchMock.mockImplementation(json(detail()));
    await renderScreen();

    expect(await screen.findByText('Design tool licence')).toBeTruthy();
    expect(screen.getByText('Rana Requester')).toBeTruthy();
    expect(screen.getByTestId('detail-status').props.children).toEqual(['Status: ', 'InReview']);
    expect(screen.getByTestId('step-1')).toBeTruthy();
    expect(screen.getByText(/Department head.*Approved/)).toBeTruthy();
    expect(screen.getByText(/IT manager.*Pending/)).toBeTruthy();
    expect(urls()).toEqual([`http://proxy.test/api/service-requests/${ID}`]);
  });

  it('shows a loading state before the first response', async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    await renderScreen();
    expect(screen.getByTestId('detail-loading')).toBeTruthy();
  });

  it('labels the triage as an advisory suggestion', async () => {
    fetchMock.mockImplementation(json(detail({ triage })));
    await renderScreen();

    expect(await screen.findByText('Suggestion (advisory, not a decision)')).toBeTruthy();
    expect(screen.getByText('Needs a licence for the design tool.')).toBeTruthy();
    expect(screen.getByText(/Priority: High/)).toBeTruthy();
    expect(screen.getByText(/Route: Department head → IT manager/)).toBeTruthy();
    expect(screen.getByText(/Confidence: 80%/)).toBeTruthy();
  });

  it('shows "No suggestion yet" when triage is null', async () => {
    fetchMock.mockImplementation(json(detail()));
    await renderScreen();
    expect(await screen.findByTestId('triage-none')).toBeTruthy();
  });

  it('shows the audit trail in the order the API sent it, not sorted by time', async () => {
    // The API orders by Sequence, so equal or odd timestamps can occur; these are deliberately not in time order.
    const trail = [
      audit('Submitted', 'Zed First', '2026-11-16T10:00:05Z', { statusBefore: null, statusAfter: 'Submitted' }),
      audit('StepEscalated', 'Amy Second', '2026-11-16T10:00:00Z'),
      audit('StepApproved', 'Bob Third', '2026-11-15T09:00:00Z', { details: 'Step 1 approved.' }),
    ];
    fetchMock.mockImplementation(json(detail({ auditTrail: trail })));
    await renderScreen();

    await screen.findByTestId('audit-0');
    const names = screen.getAllByTestId(/^audit-\d+$/).map((n) => n.props.testID);
    expect(names).toEqual(['audit-0', 'audit-1', 'audit-2']);
    expect(screen.getByTestId('audit-0').props.children.join('')).toContain('Zed First');
    expect(screen.getByTestId('audit-1').props.children.join('')).toContain('Amy Second');
    expect(screen.getByTestId('audit-2').props.children.join('')).toContain('Bob Third');
    expect(screen.getByText(/Step 1 approved\./)).toBeTruthy();
    // No "before" status for the first entry.
    expect(screen.getByTestId('audit-0').props.children.join('')).not.toContain('null');
  });

  it('displays an unknown position as-is', async () => {
    fetchMock.mockImplementation(json(detail({ steps: [step(1, 'RegionalDirector')] })));
    await renderScreen();
    expect(await screen.findByText(/RegionalDirector/)).toBeTruthy();
  });

  it.each([
    [404, 'This request no longer exists.'],
    [500, 'Something went wrong loading this request.'],
  ])('shows a message for %s and a retry button', async (code, message) => {
    fetchMock.mockImplementation(json({ title: 'x', stack: 'at secret.cs:1' }, code));
    await renderScreen();
    const error = await screen.findByTestId('detail-error');
    expect(error.props.children).toBe(message);
    expect(screen.queryByText(/secret/)).toBeNull();
    expect(screen.getByText('Try again')).toBeTruthy();
  });

  it('shows a network message when no response arrives, and retry loads the request', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Network request failed'));
    fetchMock.mockImplementation(json(detail()));
    await renderScreen();
    expect((await screen.findByTestId('detail-error')).props.children).toBe("Can't reach the server.");

    await act(async () => {
      fireEvent.press(screen.getByText('Try again'));
    });
    expect(await screen.findByText('Design tool licence')).toBeTruthy();
    expect(screen.queryByTestId('detail-error')).toBeNull();
  });

  it('a slower earlier response cannot overwrite a newer one', async () => {
    let releaseSlow!: () => void;
    const slow = new Promise<Response>((resolve) => {
      releaseSlow = () => resolve(new Response(JSON.stringify(detail({ title: 'OLD title' })), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    });
    fetchMock.mockReturnValueOnce(Promise.resolve(new Response('{}', { status: 500 }))); // first load fails
    fetchMock.mockReturnValueOnce(slow); // retry 1, slow
    fetchMock.mockImplementationOnce(json(detail({ title: 'NEW title' }))); // retry 2, fast
    await renderScreen();
    await screen.findByTestId('detail-error');

    await act(async () => {
      fireEvent.press(screen.getByText('Try again'));
    });
    await act(async () => {
      fireEvent.press(screen.getByText('Try again'));
    });
    expect(await screen.findByText('NEW title')).toBeTruthy();
    await act(async () => {
      releaseSlow();
    });
    expect(screen.getByText('NEW title')).toBeTruthy();
    expect(screen.queryByText('OLD title')).toBeNull();
  });
});

describe('RequestDetailScreen lifecycle', () => {
  it('a 401 shows no error text (the interceptor ends the session)', async () => {
    fetchMock.mockImplementation(() => Promise.resolve(new Response(null, { status: 401 })));
    await renderScreen();
    await act(async () => {});
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('detail-error')).toBeNull();
    expect(screen.queryByText(/something went wrong/i)).toBeNull();
  });

  it('clears the previous request when the id changes, while the new one is pending', async () => {
    const OTHER = '55555555-5555-4555-8555-555555555555';
    fetchMock.mockImplementationOnce(json(detail({ title: 'First request' })));
    const view = await renderScreen();
    expect(await screen.findByText('First request')).toBeTruthy();

    fetchMock.mockReturnValueOnce(new Promise(() => {}));
    await view.rerender(element(OTHER));
    expect(screen.queryByText('First request')).toBeNull();
    expect(screen.getByTestId('detail-loading')).toBeTruthy();
    expect(urls()[1]).toBe(`http://proxy.test/api/service-requests/${OTHER}`);
  });

  it('a response arriving after unmount causes no state update or console error', async () => {
    const errorSpy = jest.spyOn(console, 'error').mockImplementation(() => {});
    const warnSpy = jest.spyOn(console, 'warn').mockImplementation(() => {});
    let release!: () => void;
    fetchMock.mockReturnValueOnce(new Promise<Response>((resolve) => {
      release = () => resolve(new Response(JSON.stringify(detail()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }));
    const view = await renderScreen();
    await view.unmount();
    await act(async () => {
      release();
    });
    expect(errorSpy).not.toHaveBeenCalled();
    expect(warnSpy).not.toHaveBeenCalled();
    errorSpy.mockRestore();
    warnSpy.mockRestore();
  });
});

describe('detailErrorMessage', () => {
  it('covers network, 404 and everything else', () => {
    expect(detailErrorMessage(undefined)).toBe("Can't reach the server.");
    expect(detailErrorMessage(404)).toBe('This request no longer exists.');
    expect(detailErrorMessage(403)).toBe('Something went wrong loading this request.');
  });
});
