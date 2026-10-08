import { useEffect, useState } from 'react';

export type Route =
  | { name: 'my-games' }
  | { name: 'create' }
  | { name: 'discover' }
  | { name: 'game'; id: string }
  | { name: 'auth-callback' }
  | { name: 'not-found' };

export function parseRoute(pathname: string): Route {
  if (pathname === '/' || pathname === '') return { name: 'my-games' };
  if (pathname === '/new') return { name: 'create' };
  if (pathname === '/discover') return { name: 'discover' };
  if (pathname === '/auth/callback') return { name: 'auth-callback' };
  const game = /^\/games\/([0-9a-fA-F-]{36})\/?$/.exec(pathname);
  if (game) return { name: 'game', id: game[1] };
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
  const [route, setRoute] = useState(() => parseRoute(window.location.pathname));
  useEffect(() => {
    const onChange = () => setRoute(parseRoute(window.location.pathname));
    window.addEventListener('popstate', onChange);
    return () => window.removeEventListener('popstate', onChange);
  }, []);
  return route;
}
