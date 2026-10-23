import { SessionManager, type IdentityProvider, type Tokens, type TokenStore } from '../src/auth/session';
const token: Tokens = { accessToken: 'fixture-access', refreshToken: 'fixture-refresh', expiresAt: 200000 };
function fixture() {
  let value: string | null = null;
  const store: TokenStore = { read: jest.fn(async () => value), write: jest.fn(async next => { value = next; }), clear: jest.fn(async () => { value = null; }) };
  const provider: IdentityProvider = { login: jest.fn(async () => token), refresh: jest.fn(async () => ({ ...token, accessToken: 'rotated' })), logout: jest.fn(async () => {}) };
  const session = new SessionManager(store, provider, 'qa', () => 1000);
  return { store, provider, session, saved: () => value };
}
test('restores only the configured environment and never restores corrupt credentials', async () => {
  const f = fixture();
  await f.store.write(JSON.stringify({ context: 'production', tokens: token }));
  await f.session.restore();
  expect(f.session.state).toBe('signed-out');
  expect(f.saved()).toBeNull();
  await f.store.write('broken json');
  await f.session.restore();
  expect(f.session.state).toBe('signed-out');
});
test('login stores a session and valid access tokens do not refresh', async () => {
  const f = fixture();
  await f.session.login();
  expect(await f.session.token()).toBe('fixture-access');
  expect(f.provider.refresh).not.toHaveBeenCalled();
  const restored = new SessionManager(f.store, f.provider, 'qa', () => 1000);
  await restored.restore();
  expect(restored.state).toBe('signed-in');
});
test('concurrent refreshes share one exchange', async () => {
  const f = fixture();
  await f.session.login();
  expect(await Promise.all([f.session.token(true), f.session.token(true)])).toEqual(['rotated', 'rotated']);
  expect(f.provider.refresh).toHaveBeenCalledTimes(1);
});
test('late refresh cannot restore a signed-out session', async () => {
  const f = fixture();
  let finish!: (value: Tokens) => void;
  f.provider.refresh = jest.fn(() => new Promise(resolve => { finish = resolve; }));
  await f.session.login();
  const refreshing = f.session.token(true);
  await f.session.logout();
  finish(token);
  expect(await refreshing).toBeNull();
  expect(f.session.state).toBe('signed-out');
  expect(f.saved()).toBeNull();
});
test('late login cannot overwrite logout', async () => {
  const f = fixture();
  let finish!: (value: Tokens) => void;
  f.provider.login = () => new Promise(resolve => { finish = resolve; });
  const signingIn = f.session.login();
  await f.session.clear();
  finish(token);
  await signingIn;
  expect(f.saved()).toBeNull();
  expect(f.session.state).toBe('signed-out');
});
test('logout clears credentials even if provider logout fails', async () => {
  const f = fixture();
  await f.session.login();
  f.provider.logout = async () => { throw new Error('offline'); };
  await expect(f.session.logout()).rejects.toThrow('offline');
  expect(f.saved()).toBeNull();
  expect(f.session.state).toBe('signed-out');
});
test('a stale 401 does not invalidate a newer token', async () => {
  const f = fixture();
  await f.session.login();
  await f.session.token(true);
  await f.session.invalidate('fixture-access');
  expect(f.session.state).toBe('signed-in');
});
test('a failed refresh requires authentication again', async () => {
  const f = fixture();
  await f.session.login();
  f.provider.refresh = async () => { throw new Error('invalid_grant'); };
  expect(await f.session.token(true)).toBeNull();
  expect(f.saved()).toBeNull();
});
