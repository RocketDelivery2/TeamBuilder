export interface Tokens { accessToken: string; refreshToken?: string; idToken?: string; expiresAt: number }
export interface TokenStore { read(): Promise<string | null>; write(value: string): Promise<void>; clear(): Promise<void> }
export interface IdentityProvider { login(): Promise<Tokens>; refresh(token: string): Promise<Tokens>; logout(tokens: Tokens | null): Promise<void> }
export type SessionState = 'restoring' | 'signed-out' | 'signed-in' | 'unavailable';
export class SessionManager {
  private tokens: Tokens | null = null;
  private epoch = 0;
  private writes: Promise<void> = Promise.resolve();
  private refreshTask?: Promise<string | null>;
  private loginTask?: Promise<void>;
  private listeners = new Set<() => void>();
  state: SessionState = 'restoring';
  constructor(private readonly store: TokenStore, private readonly provider: IdentityProvider,
    private readonly context: string, private readonly now = Date.now) {}
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  snapshot = () => this.state;
  private emit(state: SessionState) { this.state = state; this.listeners.forEach(listener => listener()); }
  private persist(value: Tokens | null, epoch: number) {
    const job = this.writes.catch(() => {}).then(async () => {
      if (epoch !== this.epoch) return;
      if (value) await this.store.write(JSON.stringify({ context: this.context, tokens: value }));
      else await this.store.clear();
    });
    this.writes = job;
    return job;
  }
  async restore() {
    const epoch = this.epoch;
    try {
      const raw = await this.store.read();
      if (epoch !== this.epoch) return;
      const saved: unknown = raw ? JSON.parse(raw) : null;
      if (saved && typeof saved === 'object' && 'context' in saved && saved.context === this.context &&
          'tokens' in saved && isTokens(saved.tokens)) this.tokens = saved.tokens;
      if (this.tokens) await this.token();
      else await this.persist(null, epoch);
    } catch { if (epoch === this.epoch) {
      this.tokens = null;
      try { await this.persist(null, epoch); } catch { this.emit('unavailable'); return; }
    } }
    if (epoch === this.epoch) this.emit(this.tokens ? 'signed-in' : 'signed-out');
  }
  login(): Promise<void> {
    if (this.loginTask) return this.loginTask;
    const epoch = ++this.epoch;
    this.loginTask = (async () => {
      const tokens = await this.provider.login();
      if (epoch !== this.epoch) return;
      if (!isTokens(tokens)) throw new Error('The identity provider returned an invalid token response.');
      await this.persist(tokens, epoch);
      if (epoch !== this.epoch) return;
      this.tokens = tokens;
      this.emit('signed-in');
    })().finally(() => { this.loginTask = undefined; });
    return this.loginTask;
  }
  async token(force = false): Promise<string | null> {
    if (!this.tokens) return null;
    if (!force && this.tokens.expiresAt > this.now() + 60000) return this.tokens.accessToken;
    if (this.refreshTask) return this.refreshTask;
    const epoch = this.epoch, previous = this.tokens;
    if (!previous.refreshToken) { await this.clear(); return null; }
    this.refreshTask = (async () => {
      try {
        const result = await this.provider.refresh(previous.refreshToken!);
        if (epoch !== this.epoch) return null;
        const next = { ...result, refreshToken: result.refreshToken ?? previous.refreshToken };
        if (!isTokens(next)) throw new Error('Invalid token response.');
        await this.persist(next, epoch);
        if (epoch !== this.epoch) return null;
        this.tokens = next;
        return next.accessToken;
      } catch {
        if (epoch === this.epoch) await this.clear();
        return null;
      }
    })().finally(() => { this.refreshTask = undefined; });
    return this.refreshTask;
  }
  async clear() {
    const epoch = ++this.epoch;
    this.tokens = null;
    this.emit('signed-out');
    try { await this.persist(null, epoch); } catch (error) { this.emit('unavailable'); throw error; }
  }
  async invalidate(accessToken: string | null) {
    if (this.tokens?.accessToken === accessToken) await this.clear();
  }
  async logout() {
    const previous = this.tokens;
    await this.clear();
    await this.provider.logout(previous);
  }
}
function isTokens(value: unknown): value is Tokens {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  return typeof v.accessToken === 'string' && v.accessToken.length > 0 &&
    typeof v.expiresAt === 'number' && Number.isFinite(v.expiresAt) &&
    (v.refreshToken === undefined || typeof v.refreshToken === 'string') &&
    (v.idToken === undefined || typeof v.idToken === 'string');
}
