export interface MobileConfig {
  environment: 'local' | 'qa' | 'production';
  apiBaseUrl: string;
  authority: string;
  clientId: string;
  scopes: string[];
  redirectUri: string;
}
export function parseConfig(env: Record<string, string | undefined>): MobileConfig {
  const environment = env.EXPO_PUBLIC_ENVIRONMENT ?? 'local';
  if (!['local', 'qa', 'production'].includes(environment)) throw new Error('Unknown app environment.');
  const apiBaseUrl = (env.EXPO_PUBLIC_API_BASE_URL ?? '').replace(/\/+$/, '');
  const authority = (env.EXPO_PUBLIC_OIDC_AUTHORITY ?? '').replace(/\/+$/, '');
  const clientId = env.EXPO_PUBLIC_OIDC_CLIENT_ID?.trim() ?? '';
  const scopes = (env.EXPO_PUBLIC_OIDC_SCOPE ?? '').trim().split(/\s+/).filter(Boolean);
  for (const [label, value] of [['API URL', apiBaseUrl], ['OIDC authority', authority]]) {
    let url: URL;
    try { url = new URL(value!); } catch { throw new Error(label + ' is not configured.'); }
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) {
      throw new Error(label + ' must be an HTTPS URL without credentials, query, or fragment.');
    }
  }
  if (!clientId || !scopes.includes('openid')) throw new Error('Configure the public OIDC client ID and scopes, including openid and the API scope.');
  return { environment: environment as MobileConfig['environment'], apiBaseUrl, authority, clientId, scopes,
    redirectUri: environment === 'production' ? 'teambuilder://auth/callback' : 'teambuilder-qa://auth/callback' };
}
// Expo only inlines statically referenced EXPO_PUBLIC variables.
export function runtimeConfig(): MobileConfig {
  return parseConfig({
    EXPO_PUBLIC_ENVIRONMENT: process.env.EXPO_PUBLIC_ENVIRONMENT,
    EXPO_PUBLIC_API_BASE_URL: process.env.EXPO_PUBLIC_API_BASE_URL,
    EXPO_PUBLIC_OIDC_AUTHORITY: process.env.EXPO_PUBLIC_OIDC_AUTHORITY,
    EXPO_PUBLIC_OIDC_CLIENT_ID: process.env.EXPO_PUBLIC_OIDC_CLIENT_ID,
    EXPO_PUBLIC_OIDC_SCOPE: process.env.EXPO_PUBLIC_OIDC_SCOPE,
  });
}
