import { useEffect, useState } from 'react';

export type Route =
  | { name: 'my-games' }
  | { name: 'create' }
  | { name: 'discover' }
  | { name: 'notifications' }
  | { name: 'game'; id: string; visit?: string }
  | { name: 'auth-callback' }
  | { name: 'not-found' };

export function parseRoute(pathname: string, search = ''): Route {
  if (pathname === '/' || pathname === '') return { name: 'my-games' };
  if (pathname === '/new') return { name: 'create' };
  if (pathname === '/discover') return { name: 'discover' };
  if (pathname === '/notifications') return { name: 'notifications' };
  if (pathname === '/auth/callback') return { name: 'auth-callback' };
  const game = /^\/games\/([0-9a-fA-F-]{36})\/?$/.exec(pathname);
  // A notification click (?n=…) into a game is its own visit, even when that game is already
  // open in this tab: the page remounts, re-reads the roster and reports the open.
  if (game) return { name: 'game', id: game[1], visit: new URLSearchParams(search).has('n') ? search : undefined };
  return { name: 'not-found' };
}

export function gamePath(id: string): string {
  return `/games/${id}`;
}

/** Absolute deep link to a game, for sharing. */
export function gameUrl(id: string, origin: string = window.location.origin): string {
  return `${origin}${gamePath(id)}`;
}

export function navigate(path: string): void {
  window.history.pushState(null, '', path);
  window.dispatchEvent(new PopStateEvent('popstate'));
}

/** A two-screen app does not need a router library: history API plus a small parser. */
export function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.pathname, window.location.search));
  useEffect(() => {
    const onChange = () => setRoute(parseRoute(window.location.pathname, window.location.search));
    window.addEventListener('popstate', onChange);
    return () => window.removeEventListener('popstate', onChange);
  }, []);
  return route;
}
