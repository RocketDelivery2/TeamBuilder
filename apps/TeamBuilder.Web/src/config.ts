export interface ClientEnv {
  DEV?: boolean;
  VITE_API_BASE_URL?: string;
  VITE_OIDC_AUTHORITY?: string;
  VITE_OIDC_CLIENT_ID?: string;
  VITE_OIDC_SCOPE?: string;
  VITE_ALLOW_DEV_TOKEN?: string;
}

export interface ClientConfig {
  apiBaseUrl: string;
  oidc?: { authority: string; clientId: string; scope: string };
  /**
   * Manual bearer-token mode: always on under `vite` dev, and in a built bundle only with the
   * explicit VITE_ALLOW_DEV_TOKEN=true opt-in (never silently in production builds).
   */
  devTokenAllowed: boolean;
}

export function resolveConfig(env: ClientEnv): ClientConfig {
  const authority = env.VITE_OIDC_AUTHORITY?.trim();
  const clientId = env.VITE_OIDC_CLIENT_ID?.trim();
  return {
    apiBaseUrl: env.VITE_API_BASE_URL?.trim() ?? '',
    oidc: authority && clientId ? { authority, clientId, scope: env.VITE_OIDC_SCOPE?.trim() || 'openid profile' } : undefined,
    devTokenAllowed: env.DEV === true || env.VITE_ALLOW_DEV_TOKEN === 'true',
  };
}

export const config = resolveConfig(import.meta.env as ClientEnv);
