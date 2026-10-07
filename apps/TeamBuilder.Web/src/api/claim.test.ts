import { describe, expect, it, vi } from 'vitest';
import { claimWithRetry, MAX_CLAIM_RETRIES } from './claim';
import { ApiError, type ApiResponse } from './http';
import type { RosterAssignment } from './types';

const assignment = { id: 'a1', occurrenceId: 'o1', playerId: 'p1', username: 'me', status: 2 } as RosterAssignment;
const ok = (status: number): ApiResponse<RosterAssignment> => ({ status, data: assignment });
const conflict = (code: string) => new ApiError(409, { status: 409, code });
const noSleep = vi.fn(async () => {});

describe('claimWithRetry', () => {
  it('201 is joined', async () => {
    const claim = vi.fn(async () => ok(201));
    await expect(claimWithRetry(claim, { sleep: noSleep })).resolves.toEqual({ kind: 'joined', assignment });
    expect(claim).toHaveBeenCalledTimes(1);
  });

  it('200 is the idempotent existing claim', async () => {
    const claim = vi.fn(async () => ok(200));
    await expect(claimWithRetry(claim, { sleep: noSleep })).resolves.toEqual({ kind: 'already-joined', assignment });
  });

  it('never retries RequirementFull', async () => {
    const claim = vi.fn(async () => { throw conflict('RequirementFull'); });
    await expect(claimWithRetry(claim, { sleep: noSleep, maxRetries: 3 })).resolves.toEqual({ kind: 'full' });
    expect(claim).toHaveBeenCalledTimes(1);
  });

  it('treats AlreadyParticipating as already joined without retrying', async () => {
    const claim = vi.fn(async () => { throw conflict('AlreadyParticipating'); });
    await expect(claimWithRetry(claim, { sleep: noSleep })).resolves.toEqual({ kind: 'already-joined' });
    expect(claim).toHaveBeenCalledTimes(1);
  });

  it('retries RosterChanged a bounded number of times with jittered back-off, then reports it', async () => {
    const sleep = vi.fn(async (_ms: number) => {});
    const claim = vi.fn(async () => { throw conflict('RosterChanged'); });

    const outcome = await claimWithRetry(claim, { sleep, maxRetries: 2, baseDelayMs: 100, random: () => 0.5 });

    expect(outcome).toEqual({ kind: 'roster-changed', attempts: 3 });
    expect(claim).toHaveBeenCalledTimes(3);
    expect(sleep.mock.calls.map(([ms]) => ms)).toEqual([150, 250]);
  });

  it('caps retries at three whatever the caller asks', async () => {
    const claim = vi.fn(async () => { throw conflict('RosterChanged'); });
    await claimWithRetry(claim, { sleep: noSleep, maxRetries: 50 });
    expect(claim).toHaveBeenCalledTimes(MAX_CLAIM_RETRIES + 1);
  });

  it('succeeds when a retry wins', async () => {
    const claim = vi.fn()
      .mockRejectedValueOnce(conflict('RosterChanged'))
      .mockResolvedValueOnce(ok(201));
    await expect(claimWithRetry(claim, { sleep: noSleep })).resolves.toEqual({ kind: 'joined', assignment });
    expect(claim).toHaveBeenCalledTimes(2);
  });

  it('stops retrying when a retry finds the roster full', async () => {
    const claim = vi.fn()
      .mockRejectedValueOnce(conflict('RosterChanged'))
      .mockRejectedValueOnce(conflict('RequirementFull'));
    await expect(claimWithRetry(claim, { sleep: noSleep, maxRetries: 3 })).resolves.toEqual({ kind: 'full' });
    expect(claim).toHaveBeenCalledTimes(2);
  });

  it('rethrows non-roster errors (403, OccurrenceClosed) without retrying', async () => {
    const forbidden = vi.fn(async () => { throw new ApiError(403); });
    await expect(claimWithRetry(forbidden, { sleep: noSleep })).rejects.toBeInstanceOf(ApiError);
    expect(forbidden).toHaveBeenCalledTimes(1);

    const closed = vi.fn(async () => { throw conflict('OccurrenceClosed'); });
    await expect(claimWithRetry(closed, { sleep: noSleep })).rejects.toMatchObject({ code: 'OccurrenceClosed' });
    expect(closed).toHaveBeenCalledTimes(1);
  });
});
