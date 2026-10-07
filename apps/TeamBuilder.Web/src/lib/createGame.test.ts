import { describe, expect, it } from 'vitest';
import { buildCreateEventRequest, defaultRequiredPlayersFor, initialCreateGameForm, nextWednesday, type CreateGameForm } from './createGame';

const form = (overrides: Partial<CreateGameForm> = {}): CreateGameForm => ({
  activity: 'basketball',
  title: '  Wednesday Night Hoops ',
  date: '2026-10-14',
  startTime: '20:00',
  durationMinutes: 120,
  location: ' Rec Center ',
  requiredPlayers: 10,
  hostIsPlaying: true,
  ...overrides,
});

describe('buildCreateEventRequest', () => {
  it('creates the occurrence and one participant requirement in one request', () => {
    const request = buildCreateEventRequest(form());
    const start = new Date('2026-10-14T20:00:00');

    expect(request).toEqual({
      name: 'Wednesday Night Hoops',
      category: 'basketball',
      eventDateUtc: start.toISOString(),
      scheduledEndUtc: new Date(start.getTime() + 120 * 60_000).toISOString(),
      location: 'Rec Center',
      rosterRequirements: [{ roleCode: 'participant', requiredCount: 10 }],
      hostParticipates: true,
    });
  });

  it('sends hostParticipates=false for an organizer-only host', () => {
    expect(buildCreateEventRequest(form({ hostIsPlaying: false })).hostParticipates).toBe(false);
  });

  it('passes the chosen count through; nothing is basketball-specific in the request', () => {
    const request = buildCreateEventRequest(form({ activity: 'volleyball', requiredPlayers: 12 }));
    expect(request.rosterRequirements).toEqual([{ roleCode: 'participant', requiredCount: 12 }]);
    expect(request.category).toBe('volleyball');
  });

  it('omits an empty location', () => {
    expect(buildCreateEventRequest(form({ location: '   ' }))).not.toHaveProperty('location');
  });

  it('rejects a non-positive player count', () => {
    expect(() => buildCreateEventRequest(form({ requiredPlayers: 0 }))).toThrow();
  });
});

describe('form defaults', () => {
  it('defaults basketball to 10 players and the host playing, as a UI convenience', () => {
    const initial = initialCreateGameForm(new Date(2026, 9, 7));
    expect(initial.requiredPlayers).toBe(10);
    expect(initial.hostIsPlaying).toBe(true);
    expect(initial.startTime).toBe('20:00');
    expect(defaultRequiredPlayersFor('basketball')).toBe(10);
  });

  it('picks the next Wednesday', () => {
    expect(nextWednesday(new Date(2026, 9, 7))).toBe('2026-10-07'); // a Wednesday
    expect(nextWednesday(new Date(2026, 9, 8))).toBe('2026-10-14');
  });
});
