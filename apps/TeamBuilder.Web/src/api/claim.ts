import { ApiError } from './http';
import { RosterCode } from './conflicts';
import type { ApiResponse } from './http';
import type { RosterAssignment } from './types';

export type ClaimOutcome =
  | { kind: 'joined'; assignment: RosterAssignment }
  | { kind: 'already-joined'; assignment?: RosterAssignment }
  | { kind: 'full' }
  | { kind: 'roster-changed'; attempts: number };

export interface ClaimRetryOptions {
  /** Retries after the first attempt, only for 409 RosterChanged. Bounded at 3. */
  maxRetries?: number;
  /** Base delay in ms; each retry waits base * attempt plus jitter. */
  baseDelayMs?: number;
  sleep?: (ms: number) => Promise<void>;
  random?: () => number;
}

export const MAX_CLAIM_RETRIES = 3;

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Claims a roster spot with the client conflict policy:
 * - 201 joined; 200 is the caller's existing claim (idempotent);
 * - 409 RequirementFull is final: never retried;
 * - 409 AlreadyParticipating means the caller is already in: the caller refreshes;
 * - 409 RosterChanged is retried at most `maxRetries` (<= 3) times with a small jittered
 *   back-off, then reported so the UI refreshes and explains that the roster changed.
 * Any other error is rethrown for the generic error handling.
 */
export async function claimWithRetry(
  claim: () => Promise<ApiResponse<RosterAssignment>>,
  options: ClaimRetryOptions = {},
): Promise<ClaimOutcome> {
  const maxRetries = Math.min(Math.max(options.maxRetries ?? 2, 0), MAX_CLAIM_RETRIES);
  const baseDelayMs = options.baseDelayMs ?? 150;
  const sleep = options.sleep ?? defaultSleep;
  const random = options.random ?? Math.random;

  for (let attempt = 0; ; attempt++) {
    try {
      const response = await claim();
      return response.status === 201
        ? { kind: 'joined', assignment: response.data }
        : { kind: 'already-joined', assignment: response.data };
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 409) throw error;

      if (error.code === RosterCode.RequirementFull) return { kind: 'full' };
      if (error.code === RosterCode.AlreadyParticipating) return { kind: 'already-joined' };
      if (error.code !== RosterCode.RosterChanged) throw error;

      if (attempt >= maxRetries) return { kind: 'roster-changed', attempts: attempt + 1 };
      await sleep(baseDelayMs * (attempt + 1) + Math.floor(random() * baseDelayMs));
    }
  }
}
