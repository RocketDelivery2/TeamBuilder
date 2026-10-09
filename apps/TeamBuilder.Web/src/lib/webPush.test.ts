import { describe, expect, it, vi } from 'vitest';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import {
  base64UrlToBytes,
  browserPushState,
  disableBrowserPush,
  enableBrowserPush,
  navigationFromWorker,
  syncBrowserPush,
  type PushEnvironment,
} from './webPush';

const KEY = 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4';

function fakeSubscription(endpoint = 'https://fcm.googleapis.com/fcm/send/abc') {
  return {
    endpoint,
    toJSON: () => ({ endpoint, expirationTime: null, keys: { p256dh: 'p', auth: 'a' } }),
    unsubscribe: vi.fn(async () => true),
  };
}

function environment(options: { permission?: NotificationPermission; answer?: NotificationPermission; existing?: ReturnType<typeof fakeSubscription> | null } = {}) {
  let subscription = options.existing ?? null;
  const pushManager = {
    getSubscription: vi.fn(async () => subscription),
    subscribe: vi.fn(async () => (subscription = fakeSubscription())),
  };
  const registration = { pushManager };
  const store = new Map<string, string>();
  const notification = {
    permission: options.permission ?? 'default',
    requestPermission: vi.fn(async () => options.answer ?? 'granted'),
  };
  const env: PushEnvironment = {
    notification,
    serviceWorker: {
      register: vi.fn(async () => registration),
      getRegistration: vi.fn(async () => registration),
    } as unknown as PushEnvironment['serviceWorker'],
    pushSupported: true,
    storage: { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => void store.set(k, v), removeItem: (k) => void store.delete(k) },
  };
  return { env, notification, pushManager, store };
}

function api(overrides: Partial<TeamBuilderApi> = {}) {
  return {
    pushConfig: vi.fn(async () => ({ enabled: true, vapidPublicKey: KEY })),
    registerPush: vi.fn(async () => ({ id: 'd1' })),
    unregisterPush: vi.fn(async () => {}),
    ...overrides,
  } as unknown as TeamBuilderApi & Record<string, ReturnType<typeof vi.fn>>;
}

describe('webPush', () => {
  it('reports state without ever requesting permission', async () => {
    const { env, notification } = environment();
    expect(await browserPushState(api(), env)).toBe('available');
    expect(notification.requestPermission).not.toHaveBeenCalled();
  });

  it('is server-disabled when the API has no VAPID key, and unsupported without the browser APIs', async () => {
    const { env } = environment();
    expect(await browserPushState(api({ pushConfig: vi.fn(async () => ({ enabled: false })) }), env)).toBe('server-disabled');
    expect(await browserPushState(api(), { pushSupported: false })).toBe('unsupported');
  });

  it('enable: asks once, subscribes with the server key and registers the browser', async () => {
    const { env, notification, pushManager, store } = environment();
    const client = api();

    expect(await enableBrowserPush(client, env)).toBe('enabled');

    expect(notification.requestPermission).toHaveBeenCalledTimes(1);
    const options = (pushManager.subscribe.mock.calls as unknown as PushSubscriptionOptionsInit[][])[0][0];
    expect(options.userVisibleOnly).toBe(true);
    expect(Array.from(options.applicationServerKey as Uint8Array)).toEqual(Array.from(base64UrlToBytes(KEY)));
    expect(client.registerPush).toHaveBeenCalledWith({
      endpoint: 'https://fcm.googleapis.com/fcm/send/abc',
      expirationTime: null,
      keys: { p256dh: 'p', auth: 'a' },
      previousEndpoint: undefined,
    });
    expect(store.get('teambuilder.push.endpoint')).toBe('https://fcm.googleapis.com/fcm/send/abc');
  });

  it('a denied permission subscribes nothing and registers nothing', async () => {
    const { env, pushManager } = environment({ answer: 'denied' });
    const client = api();
    expect(await enableBrowserPush(client, env)).toBe('denied');
    expect(pushManager.subscribe).not.toHaveBeenCalled();
    expect(client.registerPush).not.toHaveBeenCalled();
  });

  it('sync re-registers a granted subscription and reports a rotated endpoint as previous', async () => {
    const { env, store } = environment({ permission: 'granted', existing: fakeSubscription('https://fcm.googleapis.com/fcm/send/new') });
    store.set('teambuilder.push.endpoint', 'https://fcm.googleapis.com/fcm/send/old');
    const client = api();

    await syncBrowserPush(client, env);

    expect(client.registerPush).toHaveBeenCalledWith(expect.objectContaining({
      endpoint: 'https://fcm.googleapis.com/fcm/send/new',
      previousEndpoint: 'https://fcm.googleapis.com/fcm/send/old',
    }));
  });

  it('sync never prompts when permission was not granted', async () => {
    const { env, notification } = environment({ permission: 'default' });
    const client = api();
    await syncBrowserPush(client, env);
    expect(notification.requestPermission).not.toHaveBeenCalled();
    expect(client.registerPush).not.toHaveBeenCalled();
  });

  it('disable unregisters the server copy and unsubscribes the browser', async () => {
    const existing = fakeSubscription();
    const { env, store } = environment({ permission: 'granted', existing });
    store.set('teambuilder.push.endpoint', existing.endpoint);
    const client = api();

    await disableBrowserPush(client, env);

    expect(client.unregisterPush).toHaveBeenCalledWith(existing.endpoint);
    expect(existing.unsubscribe).toHaveBeenCalled();
    expect(store.has('teambuilder.push.endpoint')).toBe(false);
  });

  it('accepts only same-origin navigation messages from the service worker', () => {
    expect(navigationFromWorker({ type: 'teambuilder:navigate', path: '/games/1?via=push' })).toBe('/games/1?via=push');
    expect(navigationFromWorker({ type: 'teambuilder:navigate', path: 'https://evil.example/' })).toBeUndefined();
    expect(navigationFromWorker({ type: 'teambuilder:navigate', path: '//evil.example/' })).toBeUndefined();
    expect(navigationFromWorker({ type: 'other', path: '/x' })).toBeUndefined();
    expect(navigationFromWorker(null)).toBeUndefined();
  });
});
