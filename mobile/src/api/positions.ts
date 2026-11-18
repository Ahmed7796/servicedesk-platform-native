// Positions are an open set: the API may add new ones without a version bump.
// Known names get a friendly label; anything else is shown as the raw string.
const LABELS: Record<string, string> = {
  DepartmentHead: 'Department head',
  ItManager: 'IT manager',
  HrManager: 'HR manager',
  FinanceManager: 'Finance manager',
  GeneralManager: 'General manager',
};

export function positionLabel(position: string): string {
  return Object.hasOwn(LABELS, position) ? LABELS[position] : position;
}
