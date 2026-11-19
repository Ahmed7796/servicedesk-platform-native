import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { NavigationContainer, createNavigationContainerRef } from '@react-navigation/native';

jest.mock('expo-secure-store', () => ({ getItemAsync: jest.fn().mockResolvedValue('jwt') }));
import { configureApi } from '../src/api/config';
import type { ApprovalStep, PendingItem, ServiceRequest, ServiceRequestDetail } from '../src/api/generated/servicedesk';
import type { RootStackParamList } from '../src/navigation/types';
import { RootNavigator } from '../src/navigation/RootNavigator';

const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();
const ID = '11111111-1111-4111-8111-111111111111';
const USER = '22222222-2222-4222-8222-222222222222';
const DEPT = '33333333-3333-4333-8333-333333333333';

const step = {
  order: 1, position: 'DepartmentHead', scope: 'RequesterDepartment', decision: 'Pending',
  decidedById: null, decidedAt: null, comment: null, escalatedFrom: null,
} satisfies ApprovalStep;
const pendingItem = {
  requestId: ID, title: 'Design tool licence', type: 'SoftwareAccess', status: 'InReview', requesterId: USER,
  requesterName: 'Rana Requester', departmentId: DEPT, createdAt: '2026-11-16T10:00:00Z', currentStep: step, triage: null,
} satisfies PendingItem;
const detail = {
  id: ID, title: 'Design tool licence', description: 'Need Figma.', type: 'SoftwareAccess', status: 'InReview',
  requesterId: USER, departmentId: DEPT, createdAt: '2026-11-16T10:00:00Z', updatedAt: '2026-11-16T11:00:00Z',
  steps: [step], requesterName: 'Rana Requester', auditTrail: [], triage: null,
} satisfies ServiceRequestDetail;
const approved = {
  id: ID, title: 'Design tool licence', description: 'Need Figma.', type: 'SoftwareAccess', status: 'Approved',
  requesterId: USER, departmentId: DEPT, createdAt: '2026-11-16T10:00:00Z', updatedAt: '2026-11-16T12:00:00Z',
  steps: [{ ...step, decision: 'Approved' }],
} satisfies ServiceRequest;

const reply = (body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
const count = (part: string) => fetchMock.mock.calls.filter((c) => (c[0] as Request).url.includes(part)).length;

beforeEach(() => {
  fetchMock.mockReset();
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  configureApi('http://proxy.test', async () => 'jwt');
});
afterEach(() => {
  globalThis.fetch = originalFetch;
});

it('approving returns to the one PendingApprovals screen, focused, and reloads it through the refresh param', async () => {
  let decided = false;
  fetchMock.mockImplementation((request: Request) => {
    if (request.url.endsWith('/approve')) {
      decided = true;
      return reply(approved);
    }
    if (request.url.includes('/notify/overdue')) return reply([]);
    if (request.url.endsWith('/pending-for-me')) return reply(decided ? [] : [pendingItem]);
    return reply(detail);
  });
  const ref = createNavigationContainerRef<RootStackParamList>();
  await render(
    <NavigationContainer ref={ref}>
      <RootNavigator />
    </NavigationContainer>,
  );

  await fireEvent.press(await screen.findByTestId(`pending-row-${ID}`));
  await fireEvent.press(await screen.findByTestId('approve-button'));

  // The refresh effect fetched the list again and it now shows the empty state.
  expect(await screen.findByTestId('pending-empty')).toBeTruthy();
  await waitFor(() => expect(count('/pending-for-me')).toBe(2));

  const routes = ref.getRootState()?.routes ?? [];
  expect(routes.map((r) => r.name)).toEqual(['PendingApprovals']);
  expect(ref.getCurrentRoute()?.name).toBe('PendingApprovals');
  expect(ref.getCurrentRoute()?.params).toEqual({ refresh: expect.any(Number) });
});
