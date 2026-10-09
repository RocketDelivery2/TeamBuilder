/**
 * The boundary between the app and how a bearer token is obtained. The API only ever sees
 * `Authorization: Bearer <token>`; nothing here weakens backend authentication.
 */
export interface AuthAdapter {
  readonly kind: 'oidc' | 'dev-token';
  getAccessToken(): Promise<string | null>;
  /**
   * Forgets the local session. With `endProviderSession` (the user pressed Sign out) an OIDC
   * adapter also ends the provider session when the provider supports it.
   */
  signOut(endProviderSession?: boolean): Promise<void>;
}

const DEV_TOKEN_KEY = 'teambuilder.devToken';
const RETURN_PATH_KEY = 'teambuilder.returnPath';

/**
 * Local-development / private-QA only: a bearer token pasted by the tester. Kept in
 * sessionStorage (this tab's session) and never in a build-time variable.
 */
export class DevTokenAuthAdapter implements AuthAdapter {
  readonly kind = 'dev-token' as const;

  constructor(private readonly storage: Storage = window.sessionStorage) {}

  static hasToken(storage: Storage = window.sessionStorage): boolean {
    return !!storage.getItem(DEV_TOKEN_KEY);
  }

  static save(token: string, storage: Storage = window.sessionStorage): void {
    storage.setItem(DEV_TOKEN_KEY, token.trim().replace(/^Bearer\s+/i, ''));
  }

  async getAccessToken(): Promise<string | null> {
    return this.storage.getItem(DEV_TOKEN_KEY);
  }

  async signOut(): Promise<void> {
    this.storage.removeItem(DEV_TOKEN_KEY);
  }
}

export function rememberReturnPath(path: string): void {
  window.sessionStorage.setItem(RETURN_PATH_KEY, path);
}

export function takeReturnPath(): string {
  const path = window.sessionStorage.getItem(RETURN_PATH_KEY) ?? '/';
  window.sessionStorage.removeItem(RETURN_PATH_KEY);
  return path.startsWith('/') && !path.startsWith('//') ? path : '/';
}
