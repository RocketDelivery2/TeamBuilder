import { ApiError } from './http';

/** Stable roster problem codes returned by the API (ProblemDetails `code`). */
export const RosterCode = {
  RequirementFull: 'RequirementFull',
  AlreadyParticipating: 'AlreadyParticipating',
  RosterChanged: 'RosterChanged',
  OccurrenceChanged: 'OccurrenceChanged',
  OccurrenceClosed: 'OccurrenceClosed',
  OccurrenceHasNoHost: 'OccurrenceHasNoHost',
  AssignmentEnded: 'AssignmentEnded',
  AssignmentTransitionInvalid: 'AssignmentTransitionInvalid',
  HostTransferTargetNotLinked: 'HostTransferTargetNotLinked',
} as const;

/**
 * What the UI should do after a failed request. The client never decides business rules;
 * it only maps the API's stable answer to a safe reaction.
 */
export type ErrorReaction =
  | 'sign-in' // 401: authentication required
  | 'forbidden' // 403: retrying will not help
  | 'not-found'
  | 'closed' // OccurrenceClosed: disable mutation controls
  | 'full' // RequirementFull: no automatic retry
  | 'already-joined' // AlreadyParticipating: refresh, the caller is already in
  | 'refresh-retry' // RosterChanged / OccurrenceChanged: refresh, user may retry safely
  | 'refresh' // AssignmentEnded and similar: refresh to show the final state
  | 'invalid' // 400 or a disallowed transition
  | 'error'; // anything unexpected (network, 5xx)

export interface InterpretedError {
  reaction: ErrorReaction;
  message: string;
  code?: string;
  /** Whether the client may automatically retry the same request (only RosterChanged on claim). */
  autoRetryable: boolean;
  /** Whether mutation controls should be disabled until a refresh says otherwise. */
  disableMutations: boolean;
}

export function interpretError(error: unknown): InterpretedError {
  if (!(error instanceof ApiError)) {
    return {
      reaction: 'error',
      message: 'Could not reach TeamBuilder. Check your connection and try again.',
      autoRetryable: false,
      disableMutations: false,
    };
  }

  const base = { code: error.code, autoRetryable: false, disableMutations: false };

  if (error.status === 401) {
    return { ...base, reaction: 'sign-in', message: 'Please sign in to continue.' };
  }
  if (error.status === 403) {
    return { ...base, reaction: 'forbidden', message: "You don't have permission to do that." };
  }
  if (error.status === 404) {
    return { ...base, reaction: 'not-found', message: 'That game or roster spot no longer exists.' };
  }
  if (error.status === 400) {
    return { ...base, reaction: 'invalid', message: error.message };
  }
  if (error.status === 409) {
    switch (error.code) {
      case RosterCode.RequirementFull:
        return { ...base, reaction: 'full', message: 'The roster is full. Watch for an open spot.' };
      case RosterCode.AlreadyParticipating:
        return { ...base, reaction: 'already-joined', message: "You're already on this game's roster." };
      case RosterCode.RosterChanged:
        return {
          ...base,
          reaction: 'refresh-retry',
          autoRetryable: true,
          message: 'The roster changed while saving. It has been refreshed; please try again.',
        };
      case RosterCode.OccurrenceChanged:
        return { ...base, reaction: 'refresh-retry', message: 'The game changed while saving. It has been refreshed; please try again.' };
      case RosterCode.OccurrenceClosed:
        return { ...base, reaction: 'closed', disableMutations: true, message: 'This game is closed; the roster can no longer change.' };
      case RosterCode.OccurrenceHasNoHost:
        return { ...base, reaction: 'forbidden', message: 'This game has no host right now.' };
      case RosterCode.AssignmentEnded:
        return { ...base, reaction: 'refresh', message: 'That player has already left this game.' };
      case RosterCode.AssignmentTransitionInvalid:
        return { ...base, reaction: 'invalid', message: error.message };
      case RosterCode.HostTransferTargetNotLinked:
        return { ...base, reaction: 'invalid', message: "That player hasn't signed in to TeamBuilder yet, so they can't host." };
      default:
        return { ...base, reaction: 'refresh', message: error.message };
    }
  }
  return { ...base, reaction: 'error', message: 'Something went wrong. Please try again.' };
}
