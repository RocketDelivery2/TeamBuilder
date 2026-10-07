import { UserManager, WebStorageStateStore } from 'oidc-client-ts';
import type { AuthAdapter } from './authAdapter';

/**
 * Authorization Code + PKCE against the configured OIDC provider (public SPA client, no
 * secret). Tokens live in sessionStorage for this tab only.
 */
export class OidcAuthAdapter implements AuthAdapter {
  readonly kind = 'oidc' as const;
  private readonly manager: UserManager;

  constructor(settings: { authority: string; clientId: string; scope: string }, origin: string = window.location.origin) {
    this.manager = new UserManager({
      authority: settings.authority,
      client_id: settings.clientId,
      scope: settings.scope,
      redirect_uri: `${origin}/auth/callback`,
      post_logout_redirect_uri: origin,
      response_type: 'code',
      userStore: new WebStorageStateStore({ store: window.sessionStorage }),
      stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
      automaticSilentRenew: false,
    });
  }

  async hasSession(): Promise<boolean> {
    const user = await this.manager.getUser();
    return !!user && !user.expired;
  }

  signIn(): Promise<void> {
    return this.manager.signinRedirect();
  }

  async completeSignIn(): Promise<void> {
    await this.manager.signinRedirectCallback();
  }

  async getAccessToken(): Promise<string | null> {
    const user = await this.manager.getUser();
    return user && !user.expired ? user.access_token : null;
  }

  async signOut(): Promise<void> {
    await this.manager.removeUser();
  }
}
