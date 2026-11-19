import Constants from 'expo-constants';

/**
 * The EAS project id that Expo needs to issue a push token. `eas init` writes it to
 * app.json (extra.eas.projectId). Null until then, and registration is skipped.
 */
export function getProjectId(): string | null {
  const id = Constants.expoConfig?.extra?.eas?.projectId ?? Constants.easConfig?.projectId;
  return typeof id === 'string' && id.trim() !== '' ? id : null;
}
