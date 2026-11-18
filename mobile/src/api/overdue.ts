import type { PendingItem } from './generated/servicedesk';
import type { OverdueStep } from './generated/notification';

/**
 * An item is overdue when `/notify/overdue` lists the same request at the same step.
 * The server already scopes that list to the caller, so assigned users are not checked here.
 */
export function isOverdue(item: Pick<PendingItem, 'requestId' | 'currentStep'>, overdue: OverdueStep[]): boolean {
  return overdue.some((s) => s.requestId === item.requestId && s.stepOrder === item.currentStep.order);
}
