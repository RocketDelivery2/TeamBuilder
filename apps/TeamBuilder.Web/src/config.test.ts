import { describe, expect, it } from 'vitest';
import { resolveConfig } from './config';

describe('resolveConfig', () => {
  it('never enables manual-token mode silently in a production build', () => {
    expect(resolveConfig({ DEV: false }).devTokenAllowed).toBe(false);
    expect(resolveConfig({ DEV: false, VITE_ALLOW_DEV_TOKEN: 'yes' }).devTokenAllowed).toBe(false);
  });

  it('enables manual-token mode under the dev server or with the explicit opt-in', () => {
    expect(resolveConfig({ DEV: true }).devTokenAllowed).toBe(true);
    expect(resolveConfig({ DEV: false, VITE_ALLOW_DEV_TOKEN: 'true' }).devTokenAllowed).toBe(true);
  });

  it('configures OIDC only when authority and client id are both present', () => {
    expect(resolveConfig({ VITE_OIDC_AUTHORITY: 'https://idp.test' }).oidc).toBeUndefined();
    expect(resolveConfig({ VITE_OIDC_AUTHORITY: 'https://idp.test', VITE_OIDC_CLIENT_ID: 'spa' }).oidc).toEqual({
      authority: 'https://idp.test',
      clientId: 'spa',
      scope: 'openid profile',
    });
  });
});

describe('resolveConfig with deployment settings (/config.js)', () => {
  const qa = { environment: 'qa', oidcAuthority: 'https://login.example.test/qa', oidcClientId: 'tb-qa-spa', oidcScope: 'openid api://tb-qa/access' };

  it('takes the OIDC client from the runtime settings so one build serves every environment', () => {
    const config = resolveConfig({ DEV: false }, qa);
    expect(config.environment).toBe('qa');
    expect(config.oidc).toEqual({ authority: qa.oidcAuthority, clientId: 'tb-qa-spa', scope: 'openid api://tb-qa/access' });
    expect(config.error).toBeUndefined();
  });

  it('never offers developer tokens in qa or production, even in a build that opted in', () => {
    expect(resolveConfig({ DEV: false, VITE_ALLOW_DEV_TOKEN: 'true' }, qa).devTokenAllowed).toBe(false);
    expect(resolveConfig({ DEV: true }, { ...qa, environment: 'Production' }).devTokenAllowed).toBe(false);
    expect(resolveConfig({ DEV: false, VITE_ALLOW_DEV_TOKEN: 'true' }, { environment: 'local' }).devTokenAllowed).toBe(true);
  });

  it('refuses a deployed environment without OIDC or with a non-https authority', () => {
    expect(resolveConfig({ DEV: false }, { environment: 'qa' }).error).toMatch(/not configured/);
    const insecure = resolveConfig({ DEV: false }, { ...qa, oidcAuthority: 'http://login.example.test' });
    expect(insecure.error).toMatch(/https/);
    expect(insecure.oidc).toBeUndefined();
  });

  it('defaults to the local environment and build-time values', () => {
    const config = resolveConfig({ VITE_API_BASE_URL: 'https://api.example.test' });
    expect(config.environment).toBe('local');
    expect(config.apiBaseUrl).toBe('https://api.example.test');
  });
});
