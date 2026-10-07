import { describe, expect, it } from 'vitest';
import { ApiError } from './http';
import { interpretError } from './conflicts';

const conflict = (code: string) => new ApiError(409, { status: 409, code, detail: `server says ${code}` });

describe('interpretError', () => {
  it.each([
    ['RequirementFull', 'full', false, false],
    ['AlreadyParticipating', 'already-joined', false, false],
    ['RosterChanged', 'refresh-retry', true, false],
    ['OccurrenceChanged', 'refresh-retry', false, false],
    ['AssignmentEnded', 'refresh', false, false],
    ['OccurrenceClosed', 'closed', false, true],
    ['OccurrenceHasNoHost', 'forbidden', false, false],
    ['AssignmentTransitionInvalid', 'invalid', false, false],
  ])('maps 409 %s to %s', (code, reaction, autoRetryable, disableMutations) => {
    const result = interpretError(conflict(code));
    expect(result.reaction).toBe(reaction);
    expect(result.code).toBe(code);
    expect(result.autoRetryable).toBe(autoRetryable);
    expect(result.disableMutations).toBe(disableMutations);
  });

  it('only RosterChanged is automatically retryable', () => {
    const retryable = ['RequirementFull', 'AlreadyParticipating', 'RosterChanged', 'OccurrenceChanged', 'AssignmentEnded', 'OccurrenceClosed']
      .filter((code) => interpretError(conflict(code)).autoRetryable);
    expect(retryable).toEqual(['RosterChanged']);
  });

  it('401 asks for sign-in', () => {
    expect(interpretError(new ApiError(401)).reaction).toBe('sign-in');
  });

  it('403 is never presented as retryable', () => {
    const result = interpretError(new ApiError(403));
    expect(result.reaction).toBe('forbidden');
    expect(result.autoRetryable).toBe(false);
  });

  it('400 shows the server detail', () => {
    const result = interpretError(new ApiError(400, { code: 'RequirementIdRequired', detail: 'RequirementId is required.' }));
    expect(result.reaction).toBe('invalid');
    expect(result.message).toBe('RequirementId is required.');
  });

  it('network failures are generic errors', () => {
    expect(interpretError(new TypeError('Failed to fetch')).reaction).toBe('error');
  });
});
