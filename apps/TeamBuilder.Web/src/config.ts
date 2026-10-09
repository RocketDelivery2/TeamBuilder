export interface ClientEnv {
  DEV?: boolean;
  VITE_API_BASE_URL?: string;
  VITE_OIDC_AUTHORITY?: string;
  VITE_OIDC_CLIENT_ID?: string;
  VITE_OIDC_SCOPE?: string;
  VITE_ALLOW_DEV_TOKEN?: string;
}

/**
 * Deployment-time settings written into `/config.js` by the web host (see
 * nginx/40-teambuilder-config.sh and docs/qa/private-qa-deployment.md), so one build artifact
 * serves QA and Production. All values are public: never a secret, token or signing key.
 */
export interface RuntimeConfig {
  /** `local`, `qa` or `production`. In `qa` and `production` manual-token sign-in is always off. */
  environment?: string;
  apiBaseUrl?: string;
  oidcAuthority?: string;
  oidcClientId?: string;
  oidcScope?: string;
}

export interface ClientConfig {
  environment: string;
  apiBaseUrl: string;
  oidc?: { authority: string; clientId: string; scope: string };
  /**
   * Manual bearer-token mode: always on under `vite` dev, and in a built bundle only with the
   * explicit VITE_ALLOW_DEV_TOKEN=true opt-in (never silently in production builds). A deployed
   * environment (`qa` or `production` in /config.js) switches it off whatever the build said.
   */
  devTokenAllowed: boolean;
  /** Why the configuration cannot be used (shown instead of the sign-in page), if anything. */
  error?: string;
}

const DEPLOYED_ENVIRONMENTS = ['qa', 'production'];

const pick = (runtime: string | undefined, build: string | undefined) => runtime?.trim() || build?.trim() || '';

export function resolveConfig(env: ClientEnv, runtime: RuntimeConfig = {}): ClientConfig {
  const environment = runtime.environment?.trim().toLowerCase() || 'local';
  const deployed = DEPLOYED_ENVIRONMENTS.includes(environment);
  const authority = pick(runtime.oidcAuthority, env.VITE_OIDC_AUTHORITY);
  const clientId = pick(runtime.oidcClientId, env.VITE_OIDC_CLIENT_ID);
  const scope = pick(runtime.oidcScope, env.VITE_OIDC_SCOPE) || 'openid profile';
  const oidc = authority && clientId ? { authority, clientId, scope } : undefined;

  let error: string | undefined;
  if (deployed && !oidc) error = 'Sign-in is not configured for this environment (missing OIDC authority or client ID).';
  else if (deployed && oidc && !authority.startsWith('https://')) error = 'The OIDC authority must use https://.';

  return {
    environment,
    apiBaseUrl: pick(runtime.apiBaseUrl, env.VITE_API_BASE_URL),
    oidc: error ? undefined : oidc,
    devTokenAllowed: !deployed && (env.DEV === true || env.VITE_ALLOW_DEV_TOKEN === 'true'),
    error,
  };
}

declare global {
  interface Window {
    __TEAMBUILDER_CONFIG__?: RuntimeConfig;
  }
}

export const config = resolveConfig(
  import.meta.env as ClientEnv,
  typeof window !== 'undefined' ? window.__TEAMBUILDER_CONFIG__ ?? {} : {},
);
