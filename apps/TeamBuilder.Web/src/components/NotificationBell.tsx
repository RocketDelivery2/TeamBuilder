import { useCallback, useEffect, useState } from 'react';
import { navigate } from '../router';
import { useSession } from '../session';
import { NOTIFICATIONS_CHANGED, unreadBadgeText, useRefreshOnFocus } from '../lib/notifications';

/** Visible-tab polling interval for the unread badge (private QA; no push yet). */
export const BELL_POLL_MS = 30_000;

/** Header bell with the caller's unread count; opens the notifications page. */
export function NotificationBell() {
  const { api } = useSession();
  const [unread, setUnread] = useState(0);

  const refresh = useCallback(() => {
    api.unreadNotificationCount().then(setUnread, () => {
      // A failed badge poll is not worth an error banner; the next poll retries.
    });
  }, [api]);

  useEffect(() => {
    refresh();
    window.addEventListener(NOTIFICATIONS_CHANGED, refresh);
    return () => window.removeEventListener(NOTIFICATIONS_CHANGED, refresh);
  }, [refresh]);
  useRefreshOnFocus(refresh, BELL_POLL_MS);

  const badge = unreadBadgeText(unread);
  return (
    <a
      href="/notifications"
      className="bell nav-link"
      aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'}
      onClick={(e) => {
        e.preventDefault();
        navigate('/notifications');
      }}
    >
      <span aria-hidden="true">🔔</span>
      {badge && <span className="bell-count" data-testid="unread-badge">{badge}</span>}
    </a>
  );
}
