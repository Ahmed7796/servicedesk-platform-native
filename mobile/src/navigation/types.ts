export type RootStackParamList = {
  Login: { notice?: string } | undefined;
  PendingApprovals: { refresh?: number } | undefined;
  RequestDetail: { requestId: string };
};
