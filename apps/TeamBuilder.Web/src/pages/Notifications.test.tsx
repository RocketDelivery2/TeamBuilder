import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { InAppNotification } from '../api/types';
import { NOTIFICATIONS_CHANGED } from '../lib/notifications';
import { SessionContext } from '../session';
import { Notifications } from './Notifications';

const occurrenceId = '22222222-2222-2222-2222-222222222222';

const item = (id: string, overrides: Partial<InAppNotification> = {}): InAppNotification => ({
  id,
  type: 'roster.vacancy',
  occurrenceId,
  rosterRequirementId: 'req',
  title: 'Basketball spot opened',
  body: 'A participant spot opened in Wednesday Basketball.',
  createdAtUtc: '2026-10-09T01:00:00Z',
  readAtUtc: null,
  isRead: false,
  ...overrides,
});

function renderWith(api: Partial<TeamBuilderApi>) {
  return render(
    <SessionContext.Provider value={{ api: api as TeamBuilderApi, me: { id: 'me', username: 'me' }, signOut: () => {} }}>
      <Notifications />
    </SessionContext.Provider>,
  );
}

describe('Notifications', () => {
  it('lists notifications, marks one read and updates the badge', async () => {
    const notifications = vi.fn().mockResolvedValue({ items: [item('n1'), item('n2', { isRead: true, readAtUtc: '2026-10-09T02:00:00Z' })], nextCursor: null });
    const markNotificationRead = vi.fn(async () => {});
    const changed = vi.fn();
    window.addEventListener(NOTIFICATIONS_CHANGED, changed);

    renderWith({ notifications, markNotificationRead });
    const rows = await screen.findAllByTestId('notification');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveClass('unread');
    expect(rows[1]).not.toHaveClass('unread');
    expect(rows[0]).toHaveTextContent('A participant spot opened in Wednesday Basketball.');

    fireEvent.click(screen.getByRole('button', { name: 'Mark read' }));
    await waitFor(() => expect(screen.getAllByTestId('notification')[0]).not.toHaveClass('unread'));
    expect(markNotificationRead).toHaveBeenCalledWith('n1');
    expect(changed).toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Mark read' })).not.toBeInTheDocument();
    window.removeEventListener(NOTIFICATIONS_CHANGED, changed);
  });

  it('opening a notification marks it read and opens the game', async () => {
    const notifications = vi.fn().mockResolvedValue({ items: [item('n1')], nextCursor: null });
    const markNotificationRead = vi.fn(async () => {});
    renderWith({ notifications, markNotificationRead });

    const link = await screen.findByRole('link', { name: /Basketball spot opened/ });
    expect(link).toHaveAttribute('href', `/games/${occurrenceId}`);
    fireEvent.click(link);

    await waitFor(() => expect(window.location.pathname).toBe(`/games/${occurrenceId}`));
    expect(markNotificationRead).toHaveBeenCalledWith('n1');
  });

  it('loads older pages with the cursor', async () => {
    const notifications = vi.fn()
      .mockResolvedValueOnce({ items: [item('n1')], nextCursor: 'c1' })
      .mockResolvedValueOnce({ items: [item('n2')], nextCursor: null });
    renderWith({ notifications });

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(screen.getAllByTestId('notification')).toHaveLength(2));
    expect(notifications).toHaveBeenLastCalledWith({ cursor: 'c1' });
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('explains the empty state', async () => {
    renderWith({ notifications: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) });
    expect(await screen.findByText(/Notify me if a spot opens/)).toBeInTheDocument();
  });
});
