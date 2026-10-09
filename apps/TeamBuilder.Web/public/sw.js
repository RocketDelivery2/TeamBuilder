/*
 * TeamBuilder service worker: Web Push display and click-through only. It caches nothing and
 * makes no API calls; the app stays fully usable without it.
 *
 * A push is a hint, never an authority: clicking opens (or focuses) the exact game page,
 * which re-reads the current roster and permissions. Nothing here claims a spot.
 */
'use strict';

const FALLBACK_PATH = '/notifications';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  event.waitUntil(showAlert(readPayload(event.data)));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const data = event.notification.data || {};
  event.waitUntil(openPath(clickPath(data.url)));
});

// The browser rotated this subscription; open TeamBuilder tabs re-register on their next sync.
self.addEventListener('pushsubscriptionchange', (event) => {
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      for (const client of windows) client.postMessage({ type: 'teambuilder:push-changed' });
    }),
  );
});

function readPayload(data) {
  if (!data) return null;
  try {
    const payload = data.json();
    return payload && typeof payload === 'object' ? payload : null;
  } catch {
    return null;
  }
}

function showAlert(payload) {
  const title = text(payload && payload.title, 'TeamBuilder');
  const options = {
    body: text(payload && payload.body, 'A spot opened. Open TeamBuilder to see if it is still available.'),
    icon: '/icons/icon-192.png',
    badge: '/icons/icon-192.png',
    data: { url: samePath(payload && payload.url) },
  };
  if (payload && typeof payload.tag === 'string') {
    // One alert per game on the device: a newer one replaces it and still alerts.
    options.tag = payload.tag;
    options.renotify = true;
  }
  return self.registration.showNotification(title, options);
}

function text(value, fallback) {
  return typeof value === 'string' && value.trim() ? value.slice(0, 300) : fallback;
}

/** Only same-origin paths: a payload can never send the user to another site. */
function samePath(url) {
  try {
    const parsed = new URL(typeof url === 'string' ? url : FALLBACK_PATH, self.location.origin);
    return parsed.origin === self.location.origin ? parsed.pathname + parsed.search : FALLBACK_PATH;
  } catch {
    return FALLBACK_PATH;
  }
}

/** Marks the visit as a push click (with the click time) so the game page can report the open. */
function clickPath(url) {
  const parsed = new URL(samePath(url), self.location.origin);
  parsed.searchParams.set('via', 'push');
  parsed.searchParams.set('t', String(Date.now()));
  return parsed.pathname + parsed.search;
}

/** Focuses an open TeamBuilder tab and routes it to the game, or opens a new window. */
async function openPath(path) {
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  const existing = windows.find((client) => {
    try {
      return new URL(client.url).origin === self.location.origin;
    } catch {
      return false;
    }
  });
  if (existing) {
    existing.postMessage({ type: 'teambuilder:navigate', path });
    try {
      await existing.focus();
    } catch {
      // Focus can be refused; the tab is routed to the game either way.
    }
    return;
  }
  await self.clients.openWindow(path);
}
