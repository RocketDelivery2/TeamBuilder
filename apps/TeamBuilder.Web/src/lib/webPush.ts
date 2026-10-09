import type { TeamBuilderApi } from '../api/teamBuilderApi';

/**
 * Browser alerts ("Notify me when a spot opens") over standard Web Push. Everything here is
 * optional: without support, permission or server configuration the app works the same and
 * alerts still appear in the bell. Permission is only ever requested from `enableBrowserPush`,
 * which callers invoke from an explicit click; nothing prompts on page load.
 */

export type BrowserPushState =
  /** This browser has no service worker / Push API / notifications (e.g. iOS Safari outside a home-screen app). */
  | 'unsupported'
  /** The server does not send Web Push (no VAPID identity configured). */
  | 'server-disabled'
  /** Never asked; the opt-in button can be shown. */
  | 'available'
  /** Permission granted and this browser is registered. */
  | 'enabled'
  /** The user blocked notifications for this site. */
  | 'denied';

export const SERVICE_WORKER_PATH = '/sw.js';
const ENDPOINT_KEY = 'teambuilder.push.endpoint';

/** The browser features this module touches, injectable for tests. */
export interface PushEnvironment {
  notification?: { readonly permission: NotificationPermission; requestPermission(): Promise<NotificationPermission> };
  serviceWorker?: Pick<ServiceWorkerContainer, 'register' | 'getRegistration'>;
  pushSupported: boolean;
  storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
}

export function browserPushEnvironment(): PushEnvironment {
  const hasWindow = typeof window !== 'undefined';
  return {
    notification: hasWindow && 'Notification' in window ? window.Notification : undefined,
    serviceWorker: hasWindow && 'serviceWorker' in navigator ? navigator.serviceWorker : undefined,
    pushSupported: hasWindow && 'PushManager' in window,
    storage: hasWindow ? safeStorage() : undefined,
  };
}

function safeStorage(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}

export function isPushSupported(env: PushEnvironment): boolean {
  return !!env.notification && !!env.serviceWorker && env.pushSupported;
}

/** Current state without prompting. */
export async function browserPushState(api: TeamBuilderApi, env: PushEnvironment = browserPushEnvironment()): Promise<BrowserPushState> {
  const config = await api.pushConfig();
  if (!config.enabled || !config.vapidPublicKey) return 'server-disabled';
  if (!isPushSupported(env)) return 'unsupported';
  const permission = env.notification!.permission;
  if (permission === 'denied') return 'denied';
  if (permission !== 'granted') return 'available';
  const registration = await env.serviceWorker!.getRegistration(SERVICE_WORKER_PATH);
  const subscription = await registration?.pushManager.getSubscription();
  return subscription ? 'enabled' : 'available';
}

/**
 * Call only from a user gesture. Asks for permission, registers the service worker, subscribes
 * with the server's key and registers this browser with the API.
 */
export async function enableBrowserPush(api: TeamBuilderApi, env: PushEnvironment = browserPushEnvironment()): Promise<BrowserPushState> {
  if (!isPushSupported(env)) return 'unsupported';
  const config = await api.pushConfig();
  if (!config.enabled || !config.vapidPublicKey) return 'server-disabled';

  const permission = await env.notification!.requestPermission();
  if (permission === 'denied') return 'denied';
  if (permission !== 'granted') return 'available';

  const registration = await env.serviceWorker!.register(SERVICE_WORKER_PATH, { scope: '/' });
  const existing = await registration.pushManager.getSubscription();
  const subscription =
    existing ??
    (await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: base64UrlToBytes(config.vapidPublicKey),
    }));
  await register(api, subscription, env);
  return 'enabled';
}

/** Turns alerts off for this browser: unsubscribes locally and deletes the server's copy. */
export async function disableBrowserPush(api: TeamBuilderApi, env: PushEnvironment = browserPushEnvironment()): Promise<void> {
  if (!isPushSupported(env)) return;
  const registration = await env.serviceWorker!.getRegistration(SERVICE_WORKER_PATH);
  const subscription = await registration?.pushManager.getSubscription();
  const endpoint = subscription?.endpoint ?? env.storage?.getItem(ENDPOINT_KEY) ?? undefined;
  if (endpoint) await api.unregisterPush(endpoint);
  await subscription?.unsubscribe();
  env.storage?.removeItem(ENDPOINT_KEY);
}

/**
 * On startup, when permission was already granted: re-register the current subscription
 * (refreshes last-seen; replaces the endpoint if the browser rotated it). Never prompts.
 */
export async function syncBrowserPush(api: TeamBuilderApi, env: PushEnvironment = browserPushEnvironment()): Promise<void> {
  if (!isPushSupported(env) || env.notification!.permission !== 'granted') return;
  const registration = await env.serviceWorker!.getRegistration(SERVICE_WORKER_PATH);
  const subscription = await registration?.pushManager.getSubscription();
  if (!subscription) return;
  const config = await api.pushConfig();
  if (!config.enabled) return;
  await register(api, subscription, env);
}

async function register(api: TeamBuilderApi, subscription: PushSubscription, env: PushEnvironment): Promise<void> {
  const json = subscription.toJSON();
  if (!json.endpoint || !json.keys?.p256dh || !json.keys?.auth) throw new Error('The browser returned an incomplete push subscription.');
  const previous = env.storage?.getItem(ENDPOINT_KEY) ?? undefined;
  await api.registerPush({
    endpoint: json.endpoint,
    expirationTime: json.expirationTime ?? null,
    keys: { p256dh: json.keys.p256dh, auth: json.keys.auth },
    previousEndpoint: previous && previous !== json.endpoint ? previous : undefined,
  });
  env.storage?.setItem(ENDPOINT_KEY, json.endpoint);
}

export function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  const raw = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

/** Message the service worker posts to an open tab after a notification click. */
export const SW_NAVIGATE = 'teambuilder:navigate';
export const SW_PUSH_CHANGED = 'teambuilder:push-changed';

/** Accepts only same-origin game/notification paths from the service worker. */
export function navigationFromWorker(data: unknown): string | undefined {
  if (!data || typeof data !== 'object') return undefined;
  const message = data as { type?: unknown; path?: unknown };
  if (message.type !== SW_NAVIGATE || typeof message.path !== 'string') return undefined;
  return message.path.startsWith('/') && !message.path.startsWith('//') ? message.path : undefined;
}
