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
