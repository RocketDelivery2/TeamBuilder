import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { TeamBuilderApi } from '../api/teamBuilderApi';
import type { OccurrenceDetail } from '../api/types';
import { SessionContext } from '../session';
import { GameDetail } from './GameDetail';

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
});
