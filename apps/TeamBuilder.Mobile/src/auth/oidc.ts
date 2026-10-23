import * as AuthSession from 'expo-auth-session';
import * as WebBrowser from 'expo-web-browser';
import * as SecureStore from 'expo-secure-store';
import type { MobileConfig } from '../config';
import { type IdentityProvider, type Tokens, SessionManager } from './session';
WebBrowser.maybeCompleteAuthSession();
function bounded<T>(promise: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const timeout = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Sign-in request timed out.')), 20000); });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
function tokens(response: AuthSession.TokenResponse): Tokens {
  if (!response.expiresIn || response.expiresIn <= 0) throw new Error('The provider must return an access-token lifetime.');
  return { accessToken: response.accessToken, refreshToken: response.refreshToken, idToken: response.idToken,
    expiresAt: (response.issuedAt + response.expiresIn) * 1000 };
}
export function createSession(config: MobileConfig): SessionManager {
  const discovery = () => bounded(AuthSession.fetchDiscoveryAsync(config.authority));
  const provider: IdentityProvider = {
    async login() {
      const document = await discovery();
      const request = new AuthSession.AuthRequest({ clientId: config.clientId, redirectUri: config.redirectUri,
        scopes: config.scopes, responseType: AuthSession.ResponseType.Code, usePKCE: true,
        prompt: AuthSession.Prompt.SelectAccount });
      const result = await request.promptAsync(document);
      if (result.type !== 'success' || !result.params.code || !request.codeVerifier) throw new Error('Sign-in was not completed.');
      return tokens(await bounded(AuthSession.exchangeCodeAsync({ clientId: config.clientId,
        redirectUri: config.redirectUri, code: result.params.code, extraParams: { code_verifier: request.codeVerifier } }, document)));
    },
    async refresh(refreshToken) { return tokens(await bounded(AuthSession.refreshAsync({ clientId: config.clientId, refreshToken }, await discovery()))); },
    async logout(previous) {
      const document = await discovery();
      if (document.revocationEndpoint && previous?.refreshToken) {
        await bounded(AuthSession.revokeAsync({ clientId: config.clientId, token: previous.refreshToken }, document));
      }
      if (document.endSessionEndpoint) {
        const url = new URL(document.endSessionEndpoint);
        url.searchParams.set('post_logout_redirect_uri', config.redirectUri);
        if (previous?.idToken) url.searchParams.set('id_token_hint', previous.idToken);
        await WebBrowser.openAuthSessionAsync(url.toString(), config.redirectUri);
      }
    },
  };
  const key = 'teambuilder.session.v1';
  return new SessionManager({
    read: () => SecureStore.getItemAsync(key),
    write: value => SecureStore.setItemAsync(key, value, { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY }),
    clear: () => SecureStore.deleteItemAsync(key),
  }, provider, JSON.stringify([config.environment, config.apiBaseUrl, config.authority, config.clientId, config.scopes]));
}
