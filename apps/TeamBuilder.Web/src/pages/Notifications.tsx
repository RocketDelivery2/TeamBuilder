import { useCallback, useEffect, useState } from 'react';
import { interpretError } from '../api/conflicts';
import type { InAppNotification } from '../api/types';
import { gamePath, navigate } from '../router';
import { useSession } from '../session';
import { notifyNotificationsChanged, useRefreshOnFocus } from '../lib/notifications';
import { PushOptIn } from '../components/PushOptIn';

/** The caller's in-app notifications, newest first. Opening one marks it read and opens the game. */
export function Notifications() {
  const { api } = useSession();
  const [items, setItems] = useState<InAppNotification[]>();
  const [cursor, setCursor] = useState<string | null>();
  const [error, setError] = useState<string>();
  const [loadingMore, setLoadingMore] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const page = await api.notifications();
      setItems(page.items);
      setCursor(page.nextCursor);
      setError(undefined);
    } catch (err) {
      setError(interpretError(err).message);
    }
  }, [api]);

  useEffect(() => {
    void refresh();
  }, [refresh]);
  const onFocus = useCallback(() => void refresh(), [refresh]);
  useRefreshOnFocus(onFocus);

  const markRead = async (item: InAppNotification) => {
    if (item.isRead) return;
    await api.markNotificationRead(item.id);
    const readAtUtc = new Date().toISOString();
    setItems((current) => current?.map((n) => (n.id === item.id ? { ...n, isRead: true, readAtUtc } : n)));
    notifyNotificationsChanged();
  };

  const open = async (item: InAppNotification) => {
    try {
      // Marks it read and records the open (refill metrics); the game page re-reads the roster.
      await api.notificationOpened(item.id, { via: 'inApp' });
      notifyNotificationsChanged();
    } catch {
      // Opening the game matters more than the read mark; it can be marked again later.
    }
    navigate(gamePath(item.occurrenceId));
  };

  const loadMore = async () => {
    if (!cursor) return;
    setLoadingMore(true);
    try {
      const page = await api.notifications({ cursor });
      setItems((current) => [...(current ?? []), ...page.items]);
      setCursor(page.nextCursor);
    } catch (err) {
      setError(interpretError(err).message);
    } finally {
      setLoadingMore(false);
    }
  };

  return (
    <section className="stack">
      <h1>Notifications</h1>
      <PushOptIn />
      {error && <p className="notice error">{error}</p>}
      {!items && !error && <p className="muted">Loading…</p>}
      {items?.length === 0 && (
        <p className="muted">
          Nothing yet. On a full game, choose “Notify me if a spot opens” and you will see it here when someone drops out.
        </p>
      )}
      <ul className="list">
        {items?.map((item) => (
          <li key={item.id} className={`card notification${item.isRead ? '' : ' unread'}`} data-testid="notification">
            <a
              href={gamePath(item.occurrenceId)}
              className="notification-link"
              onClick={(e) => {
                e.preventDefault();
                void open(item);
              }}
            >
              <strong>{item.title}</strong>
              <span>{item.body}</span>
              <span className="muted small">{new Date(item.createdAtUtc).toLocaleString()}</span>
            </a>
            {!item.isRead && (
              <button className="link small" onClick={() => void markRead(item).catch((err) => setError(interpretError(err).message))}>
                Mark read
              </button>
            )}
          </li>
        ))}
      </ul>
      {cursor && (
        <button disabled={loadingMore} onClick={() => void loadMore()}>
          Load more
        </button>
      )}
    </section>
  );
}
