import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import { notifyNotificationsChanged } from '../lib/notifications';
import { SessionContext } from '../session';
import { BELL_POLL_MS, NotificationBell } from './NotificationBell';

function renderWith(api: Partial<TeamBuilderApi>) {
  return render(
    <SessionContext.Provider value={{ api: api as TeamBuilderApi, me: { id: 'me', username: 'me' }, signOut: () => {} }}>
      <NotificationBell />
    </SessionContext.Provider>,
  );
}

const setVisibility = (state: DocumentVisibilityState) =>
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });

describe('NotificationBell', () => {
  afterEach(() => {
    vi.useRealTimers();
    setVisibility('visible');
  });

  it('shows the unread count and hides the badge at zero', async () => {
    const unreadNotificationCount = vi.fn().mockResolvedValueOnce(12).mockResolvedValue(0);
    renderWith({ unreadNotificationCount });

    expect(await screen.findByTestId('unread-badge')).toHaveTextContent('9+');
    expect(screen.getByRole('link', { name: 'Notifications, 12 unread' })).toHaveAttribute('href', '/notifications');

    await act(async () => notifyNotificationsChanged());
    await waitFor(() => expect(screen.queryByTestId('unread-badge')).not.toBeInTheDocument());
  });

  it('polls only while the tab is visible', async () => {
    vi.useFakeTimers();
    const unreadNotificationCount = vi.fn().mockResolvedValue(1);
    renderWith({ unreadNotificationCount });
    expect(unreadNotificationCount).toHaveBeenCalledTimes(1);

    await act(async () => { vi.advanceTimersByTime(BELL_POLL_MS); });
    expect(unreadNotificationCount).toHaveBeenCalledTimes(2);

    setVisibility('hidden');
    await act(async () => { vi.advanceTimersByTime(BELL_POLL_MS * 3); });
    expect(unreadNotificationCount).toHaveBeenCalledTimes(2);

    setVisibility('visible');
    await act(async () => { document.dispatchEvent(new Event('visibilitychange')); });
    expect(unreadNotificationCount).toHaveBeenCalledTimes(3);
  });
});
