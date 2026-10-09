import { beforeEach, describe, expect, it, vi } from 'vitest';
import source from '../public/sw.js?raw';

/**
 * Runs public/sw.js against a fake ServiceWorkerGlobalScope: the file is plain JS (served as
 * is, no bundling), so the test evaluates its text with `self` bound to the fake.
 */
interface FakeClient {
  url: string;
  postMessage: ReturnType<typeof vi.fn>;
  focus: ReturnType<typeof vi.fn>;
}

function load(windows: FakeClient[] = []) {
  const listeners = new Map<string, (event: unknown) => void>();
  const self = {
    location: new URL('https://teambuilder.test/sw.js'),
    addEventListener: (type: string, listener: (event: unknown) => void) => listeners.set(type, listener),
    skipWaiting: vi.fn(),
    registration: { showNotification: vi.fn(async () => {}) },
    clients: {
      claim: vi.fn(async () => {}),
      matchAll: vi.fn(async () => windows),
      openWindow: vi.fn(async () => null),
    },
  };
  new Function('self', source)(self);
  const fire = async (type: string, event: Record<string, unknown>) => {
    let pending: Promise<unknown> = Promise.resolve();
    listeners.get(type)!({ ...event, waitUntil: (p: Promise<unknown>) => (pending = p) });
    await pending;
  };
  return { self, fire };
}

const payload = {
  v: 1,
  type: 'roster.vacancy',
  notificationId: '11111111-1111-1111-1111-111111111111',
  occurrenceId: '22222222-2222-2222-2222-222222222222',
  title: 'Basketball spot opened',
  body: 'A participant spot opened in Wednesday Basketball.',
  url: '/games/22222222-2222-2222-2222-222222222222?n=11111111111111111111111111111111',
  tag: 'occurrence-22222222222222222222222222222222',
};

describe('service worker', () => {
  beforeEach(() => vi.useRealTimers());

  it('shows the sparse payload as a notification tagged per game, linking to the exact game', async () => {
    const { self, fire } = load();
    await fire('push', { data: { json: () => payload } });

    expect(self.registration.showNotification).toHaveBeenCalledWith('Basketball spot opened', expect.objectContaining({
      body: 'A participant spot opened in Wednesday Basketball.',
      tag: payload.tag,
      renotify: true,
      data: { url: payload.url },
    }));
  });

  it('never links off-origin, and survives an unreadable payload', async () => {
    const { self, fire } = load();
    await fire('push', { data: { json: () => ({ ...payload, url: 'https://evil.example/phish' }) } });
    expect(self.registration.showNotification).toHaveBeenLastCalledWith('Basketball spot opened', expect.objectContaining({ data: { url: '/notifications' } }));

    await fire('push', { data: { json: () => { throw new SyntaxError('bad'); } } });
    expect(self.registration.showNotification).toHaveBeenLastCalledWith('TeamBuilder', expect.objectContaining({ data: { url: '/notifications' } }));
  });

  it('a click routes an open TeamBuilder tab to the game and focuses it; it claims nothing', async () => {
    const tab = { url: 'https://teambuilder.test/discover', postMessage: vi.fn(), focus: vi.fn(async () => {}) };
    const { self, fire } = load([{ url: 'https://elsewhere.example/', postMessage: vi.fn(), focus: vi.fn() }, tab]);
    const close = vi.fn();

    await fire('notificationclick', { notification: { close, data: { url: payload.url } } });

    expect(close).toHaveBeenCalled();
    const message = tab.postMessage.mock.calls[0][0] as { type: string; path: string };
    expect(message.type).toBe('teambuilder:navigate');
    const path = new URL(message.path, 'https://teambuilder.test');
    expect(path.pathname).toBe('/games/22222222-2222-2222-2222-222222222222');
    expect(path.searchParams.get('n')).toBe('11111111111111111111111111111111');
    expect(path.searchParams.get('via')).toBe('push');
    expect(Number(path.searchParams.get('t'))).toBeGreaterThan(0);
    expect(tab.focus).toHaveBeenCalled();
    expect(self.clients.openWindow).not.toHaveBeenCalled();
  });

  it('opens a new window on the game when no TeamBuilder tab is open', async () => {
    const { self, fire } = load([]);
    await fire('notificationclick', { notification: { close: vi.fn(), data: { url: payload.url } } });
    const opened = (self.clients.openWindow.mock.calls as unknown as string[][])[0][0];
    expect(opened.startsWith('/games/22222222-2222-2222-2222-222222222222?')).toBe(true);
  });

  it('still routes the tab when focus is refused', async () => {
    const tab = { url: 'https://teambuilder.test/', postMessage: vi.fn(), focus: vi.fn(async () => { throw new Error('InvalidAccessError'); }) };
    const { fire } = load([tab]);
    await fire('notificationclick', { notification: { close: vi.fn(), data: { url: payload.url } } });
    expect(tab.postMessage).toHaveBeenCalled();
  });
});
