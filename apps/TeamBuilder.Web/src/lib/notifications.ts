import { useEffect } from 'react';

/** Fired after the caller reads notifications here, so the header badge updates at once. */
export const NOTIFICATIONS_CHANGED = 'teambuilder:notifications-changed';

export function notifyNotificationsChanged(): void {
  window.dispatchEvent(new Event(NOTIFICATIONS_CHANGED));
}

/** Badge text: nothing at zero, "9+" above nine. */
export function unreadBadgeText(count: number): string {
  if (count <= 0) return '';
  return count > 9 ? '9+' : String(count);
}

/**
 * Runs `refresh` when the page regains focus or becomes visible, and every `intervalMs` while
 * the tab is visible (lightweight private-QA polling; no SignalR, no push). Hidden tabs do not poll.
 */
export function useRefreshOnFocus(refresh: () => void, intervalMs?: number): void {
  useEffect(() => {
    const onVisible = () => {
      if (document.visibilityState === 'visible') refresh();
    };
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', onVisible);
    const timer = intervalMs
      ? window.setInterval(() => {
          if (document.visibilityState === 'visible') refresh();
        }, intervalMs)
      : undefined;
    return () => {
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', onVisible);
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [refresh, intervalMs]);
}
