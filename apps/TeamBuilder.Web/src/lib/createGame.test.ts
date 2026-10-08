import { describe, expect, it } from 'vitest';
import { buildCreateEventRequest, buildCreateVenueRequest, defaultRequiredPlayersFor, initialCreateGameForm, nextWednesday, type CreateGameForm } from './createGame';

const form = (overrides: Partial<CreateGameForm> = {}): CreateGameForm => ({
  activity: 'basketball',
  title: '  Wednesday Night Hoops ',
  date: '2026-10-14',
  startTime: '20:00',
  durationMinutes: 120,
  location: ' Rec Center ',
  requiredPlayers: 10,
  hostIsPlaying: true,
  useVenue: false,
  venueName: '',
  venueAddress: '',
  venueCity: '',
  venueState: '',
  venueType: 'outdoor',
  venuePrivacy: 'public',
  venueLat: '',
  venueLon: '',
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
    expect(nextWednesday(new Date(2026, 9, 7, 19, 59))).toBe('2026-10-07');
    expect(nextWednesday(new Date(2026, 9, 7, 20, 0))).toBe('2026-10-14'); // tonight's game has started
    expect(nextWednesday(new Date(2026, 9, 8))).toBe('2026-10-14');
  });
});

describe('buildCreateVenueRequest', () => {
  const withVenue = (overrides: Partial<CreateGameForm> = {}) =>
    form({ useVenue: true, venueName: ' Union Park ', venueAddress: '1501 W Randolph St', venueCity: 'Chicago', venueState: 'IL', venueLat: '41.8849', venueLon: '-87.6661', ...overrides });

  it('is null when no venue is attached, and the game keeps its free-text location', () => {
    expect(buildCreateVenueRequest(form(), 'America/Chicago')).toBeNull();
    expect(buildCreateEventRequest(form()).venueId).toBeUndefined();
    expect(buildCreateEventRequest(form()).location).toBe('Rec Center');
  });

  it('creates a public physical venue with the browser time zone and typed coordinates', () => {
    expect(buildCreateVenueRequest(withVenue(), 'America/Chicago')).toEqual({
      name: 'Union Park',
      addressLine1: '1501 W Randolph St',
      city: 'Chicago',
      stateOrProvince: 'IL',
      latitude: 41.8849,
      longitude: -87.6661,
      timeZoneId: 'America/Chicago',
      venueType: 2,
      privacyLevel: 1,
    });
  });

  it('marks a private indoor venue', () => {
    const request = buildCreateVenueRequest(withVenue({ venuePrivacy: 'private', venueType: 'indoor' }), 'America/Chicago')!;
    expect([request.privacyLevel, request.venueType]).toEqual([2, 1]);
  });

  it('refuses a venue without a name or with impossible coordinates (nothing is geocoded)', () => {
    expect(() => buildCreateVenueRequest(withVenue({ venueName: ' ' }), 'UTC')).toThrow(/name/);
    expect(() => buildCreateVenueRequest(withVenue({ venueLat: '' }), 'UTC')).toThrow(/Latitude/);
    expect(() => buildCreateVenueRequest(withVenue({ venueLat: '91' }), 'UTC')).toThrow(/Latitude/);
    expect(() => buildCreateVenueRequest(withVenue({ venueLon: '-180.5' }), 'UTC')).toThrow(/Longitude/);
  });

  it('attaches the created venue to the game request', () => {
    expect(buildCreateEventRequest(form(), 'venue-1').venueId).toBe('venue-1');
  });
});
