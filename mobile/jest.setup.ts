// Default mocks for the native push modules, which do not exist under jest. The defaults make
// registration a quiet no-op (no projectId, permission denied), so unrelated suites are unaffected.
// Suites that test push override these per test.
jest.mock('expo-constants', () => ({
  __esModule: true,
  default: { expoConfig: {}, easConfig: undefined },
}));

jest.mock('expo-notifications', () => ({
  AndroidImportance: { MAX: 5 },
  setNotificationChannelAsync: jest.fn().mockResolvedValue(null),
  getPermissionsAsync: jest.fn().mockResolvedValue({ status: 'denied' }),
  requestPermissionsAsync: jest.fn().mockResolvedValue({ status: 'denied' }),
  getExpoPushTokenAsync: jest.fn().mockResolvedValue({ data: 'ExponentPushToken[default]' }),
}));
