import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { OccurrenceDetail } from '../api/types';
import { SessionContext } from '../session';
import { GameDetail, notificationArrival } from './GameDetail';

const id = '22222222-2222-2222-2222-222222222222';

const detail = (overrides: Partial<OccurrenceDetail> = {}): OccurrenceDetail => ({
  occurrenceId: id,
  name: 'Wednesday Night Hoops',
  scheduledStartUtc: '2026-10-15T00:00:00Z',
  status: 1,
  acceptsRosterChanges: true,
  location: 'Rec Center',
  hostPlayerId: 'host',
  hostUsername: 'host',
  hostDisplayName: 'Hana Host',
  requiredCount: 10,
  supplyCount: 9,
  openQuantity: 1,
  isRosterReady: false,
  requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 9, openQuantity: 1 }],
  participants: [],
  isHost: false,
  ...overrides,
});

function renderWith(api: Partial<TeamBuilderApi>) {
  return render(
    <SessionContext.Provider value={{ api: api as TeamBuilderApi, me: { id: 'me', username: 'me' }, signOut: () => {} }}>
      <GameDetail occurrenceId={id} />
    </SessionContext.Provider>,
  );
}

describe('GameDetail', () => {
  it('joins and re-renders from a fresh read (refill visible at once)', async () => {
    const getDetail = vi.fn()
      .mockResolvedValueOnce(detail())
      .mockResolvedValue(detail({ supplyCount: 10, openQuantity: 0, isRosterReady: true, myAssignmentId: 'mine', myAssignmentStatus: 2, requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 10, openQuantity: 0 }] }));
    const claim = vi.fn(async () => ({ status: 201, data: {} as never }));

    renderWith({ getDetail, claim });
    fireEvent.click(await screen.findByRole('button', { name: 'Join game' }));

    await waitFor(() => expect(screen.getByTestId('roster-badge')).toHaveTextContent('READY'));
    expect(claim).toHaveBeenCalledWith(id, 'req');
    expect(screen.getByRole('button', { name: 'Leave game' })).toBeInTheDocument();
  });

  it('does not retry a full roster and explains it', async () => {
    const getDetail = vi.fn().mockResolvedValue(detail());
    const claim = vi.fn(async () => { throw new ApiError(409, { code: 'RequirementFull' }); });

    renderWith({ getDetail, claim });
    fireEvent.click(await screen.findByRole('button', { name: 'Join game' }));

    expect(await screen.findByRole('status')).toHaveTextContent('filled up');
    expect(claim).toHaveBeenCalledTimes(1);
  });

  it('disables mutations after OccurrenceClosed', async () => {
    const getDetail = vi.fn().mockResolvedValue(detail({ myAssignmentId: 'mine', myAssignmentStatus: 2 }));
    const leave = vi.fn(async () => { throw new ApiError(409, { code: 'OccurrenceClosed' }); });

    renderWith({ getDetail, leave });
    const button = await screen.findByRole('button', { name: 'Leave game' });
    await act(async () => { fireEvent.click(button); });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Leave game' })).toBeDisabled());
  });

  it('shows host controls to an organizer-only host', async () => {
    const getDetail = vi.fn().mockResolvedValue(detail({
      isHost: true,
      participants: [{ assignmentId: 'a1', playerId: 'p1', username: 'pat', displayName: 'Pat', status: 2, isHost: false }],
    }));

    renderWith({ getDetail });

    expect(await screen.findByRole('button', { name: 'Check in' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'No show' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Transfer hosting' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join game' })).toBeInTheDocument();
  });

  it('offers "Notify me" on a full roster and subscribes to that requirement', async () => {
    const full = { requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 10, openQuantity: 0 }], supplyCount: 10, openQuantity: 0 };
    const getDetail = vi.fn()
      .mockResolvedValueOnce(detail(full))
      .mockResolvedValue(detail({ ...full, mySubscribedRequirementIds: ['req'] }));
    const subscribe = vi.fn(async () => ({ requirementId: 'req' }) as never);

    renderWith({ getDetail, subscribe });
    expect(await screen.findByText('The roster is full.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Join game' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Notify me if a spot opens' }));

    expect(await screen.findByTestId('notify-on')).toHaveTextContent('Notifications on');
    expect(subscribe).toHaveBeenCalledWith(id, 'req');
    expect(screen.getByRole('status')).toHaveTextContent('grab it fast');
  });

  it('turns notifications off', async () => {
    const full = { requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 10, openQuantity: 0 }], supplyCount: 10, openQuantity: 0 };
    const getDetail = vi.fn()
      .mockResolvedValueOnce(detail({ ...full, mySubscribedRequirementIds: ['req'] }))
      .mockResolvedValue(detail(full));
    const unsubscribe = vi.fn(async () => {});

    renderWith({ getDetail, unsubscribe });
    fireEvent.click(await screen.findByRole('button', { name: 'Turn off' }));

    expect(await screen.findByRole('button', { name: 'Notify me if a spot opens' })).toBeInTheDocument();
    expect(unsubscribe).toHaveBeenCalledWith(id, 'req');
  });

  it('attaches "Notify me" to the full role only in a multi-role game', async () => {
    const getDetail = vi.fn().mockResolvedValue(detail({
      requirements: [
        { id: 'gk', occurrenceId: id, roleCode: 'goalkeeper', displayPosition: 'Goalkeeper', requiredCount: 2, supplyCount: 2, openQuantity: 0 },
        { id: 'field', occurrenceId: id, roleCode: 'field', displayPosition: 'Field player', requiredCount: 10, supplyCount: 8, openQuantity: 2 },
      ],
    }));
    const subscribe = vi.fn(async () => ({}) as never);

    renderWith({ getDetail, subscribe });
    expect(await screen.findByRole('button', { name: 'Join game as Field player' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Notify me if a Field player/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Notify me if a Goalkeeper spot opens' }));

    await waitFor(() => expect(subscribe).toHaveBeenCalledWith(id, 'gk'));
  });

  it('hides "Notify me" from participants and on closed games', async () => {
    const full = { requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 10, openQuantity: 0 }], supplyCount: 10, openQuantity: 0 };
    const getDetail = vi.fn().mockResolvedValue(detail({ ...full, myAssignmentId: 'mine', myAssignmentStatus: 2 }));
    const view = renderWith({ getDetail });
    expect(await screen.findByRole('button', { name: 'Leave game' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Notify me/ })).not.toBeInTheDocument();
    view.unmount();

    renderWith({ getDetail: vi.fn().mockResolvedValue(detail({ ...full, acceptsRosterChanges: false, status: 3 })) });
    expect(await screen.findByText(/This game is closed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Notify me/ })).not.toBeInTheDocument();
  });

  it('re-reads the game when the tab regains focus', async () => {
    const getDetail = vi.fn().mockResolvedValue(detail());
    renderWith({ getDetail });
    await screen.findByRole('button', { name: 'Join game' });

    await act(async () => { window.dispatchEvent(new Event('focus')); });

    expect(getDetail).toHaveBeenCalledTimes(2);
  });

  it('opened from a push: reports the open once the current roster shows, strips the link, and never claims', async () => {
    window.history.pushState(null, '', `/games/${id}?n=11111111111111111111111111111111&via=push&t=${Date.now() - 250}`);
    const getDetail = vi.fn().mockResolvedValue(detail());
    const notificationOpened = vi.fn(async () => {});
    const claim = vi.fn();

    renderWith({ getDetail, notificationOpened, claim });

    await waitFor(() => expect(notificationOpened).toHaveBeenCalledTimes(1));
    const [notificationId, opened] = (notificationOpened.mock.calls as unknown as [string, { via: string; clickToOpenMs?: number }][])[0];
    expect(notificationId).toBe('11111111111111111111111111111111');
    expect(opened.via).toBe('push');
    expect(opened.clickToOpenMs).toBeGreaterThanOrEqual(0);
    expect(window.location.search).toBe('');
    expect(screen.getByRole('button', { name: 'Join game' })).toBeEnabled();
    expect(claim).not.toHaveBeenCalled();
  });

  it('a stale alert is normal: the spot already refilled is explained from the fresh read', async () => {
    window.history.pushState(null, '', `/games/${id}?n=11111111111111111111111111111111&via=push&t=1`);
    const full = detail({ supplyCount: 10, openQuantity: 0, isRosterReady: true, requirements: [{ id: 'req', occurrenceId: id, roleCode: 'participant', requiredCount: 10, supplyCount: 10, openQuantity: 0 }] });
    renderWith({ getDetail: vi.fn().mockResolvedValue(full), notificationOpened: vi.fn(async () => {}) });

    expect(await screen.findByRole('status')).toHaveTextContent('already been taken');
    expect(screen.queryByRole('button', { name: 'Join game' })).not.toBeInTheDocument();
  });

  it('a cancelled game opened from an alert shows closed, with no join', async () => {
    window.history.pushState(null, '', `/games/${id}?n=11111111111111111111111111111111&via=push&t=1`);
    renderWith({ getDetail: vi.fn().mockResolvedValue(detail({ status: 5, acceptsRosterChanges: false })), notificationOpened: vi.fn(async () => {}) });

    expect(await screen.findByText(/This game is closed/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Join game' })).toBeDisabled();
  });

  it('parses notification arrivals defensively', () => {
    expect(notificationArrival('?n=11111111-1111-1111-1111-111111111111')).toEqual({ notificationId: '11111111-1111-1111-1111-111111111111', via: 'inApp', clickedAt: undefined });
    expect(notificationArrival('?n=../../etc&via=push')).toBeUndefined();
    expect(notificationArrival('')).toBeUndefined();
  });
});
