import { describe, expect, it, vi } from 'vitest';
import { discoverSearchParams } from '../api/teamBuilderApi';
import type { DiscoveredOccurrence } from '../api/types';
import {
  RADIUS_PRESETS_MILES,
  buildDiscoverQuery,
  currentPosition,
  formatDistance,
  formatWhenInZone,
  initialDiscoverForm,
  localityLabel,
  parseManualPoint,
  searchWindow,
  spotsLabel,
  viewerLabel,
  type DiscoverForm,
} from './discover';

const tuesdayNoon = new Date('2026-10-13T12:00:00');
const form = (overrides: Partial<DiscoverForm> = {}): DiscoverForm => ({ ...initialDiscoverForm(tuesdayNoon), date: '2026-10-14', ...overrides });

const game = (overrides: Partial<DiscoveredOccurrence> = {}): DiscoveredOccurrence => ({
  occurrenceId: '11111111-1111-1111-1111-111111111111',
  name: 'Wednesday Basketball 8 PM',
  category: 'basketball',
  status: 1,
  scheduledStartUtc: '2026-10-15T01:00:00Z',
  scheduledEndUtc: '2026-10-15T03:00:00Z',
  timeZoneId: 'America/Chicago',
  venue: { venueId: 'v1', name: 'Union Park', city: 'Chicago', stateOrProvince: 'IL', addressLine1: '1501 W Randolph St', privacyLevel: 1, distanceMiles: 2.04, isAddressMasked: false },
  roster: { totalRequiredCount: 10, totalSupplyCount: 7, totalOpenQuantity: 3, isRosterReady: false, isFull: false },
  viewer: null,
  ...overrides,
});

describe('discover defaults', () => {
  it('defaults to basketball, today, any time, 15 miles, all games', () => {
    const initial = initialDiscoverForm(tuesdayNoon);
    expect([initial.activity, initial.date, initial.timeMode, initial.radiusMiles, initial.openOnly]).toEqual(['basketball', '2026-10-13', 'any', 15, false]);
    expect(RADIUS_PRESETS_MILES).toEqual([15, 25, 50]);
  });
});

describe('searchWindow (local intent to UTC; the API never reinterprets it)', () => {
  it('date only: the whole local day', () => {
    expect(searchWindow(form(), tuesdayNoon)).toEqual({
      fromUtc: new Date('2026-10-14T00:00:00').toISOString(),
      toUtc: new Date('2026-10-15T00:00:00').toISOString(),
    });
  });

  it('evening: 5 PM to 11 PM local', () => {
    expect(searchWindow(form({ timeMode: 'evening' }), tuesdayNoon)).toEqual({
      fromUtc: new Date('2026-10-14T17:00:00').toISOString(),
      toUtc: new Date('2026-10-14T23:00:00').toISOString(),
    });
  });

  it('around 8 PM: 7 PM to 9 PM local', () => {
    expect(searchWindow(form({ timeMode: 'around', aroundTime: '20:00' }), tuesdayNoon)).toEqual({
      fromUtc: new Date('2026-10-14T19:00:00').toISOString(),
      toUtc: new Date('2026-10-14T21:00:00').toISOString(),
    });
  });

  it('starts at now for a window already under way, and refuses one that is over', () => {
    const wednesday6pm = new Date('2026-10-14T18:00:00');
    expect(searchWindow(form({ timeMode: 'evening' }), wednesday6pm).fromUtc).toBe(wednesday6pm.toISOString());
    expect(() => searchWindow(form({ timeMode: 'around', aroundTime: '08:00' }), wednesday6pm)).toThrow(/passed/);
  });
});

describe('parseManualPoint', () => {
  it('accepts coordinates in range', () => {
    expect(parseManualPoint(' 41.8781 ', '-87.6298')).toEqual({ lat: 41.8781, lon: -87.6298 });
    expect(parseManualPoint('-90', '180')).toEqual({ lat: -90, lon: 180 });
  });

  it.each([
    ['', '0', /Latitude/],
    ['90.1', '0', /Latitude/],
    ['abc', '0', /Latitude/],
    ['0', '', /Longitude/],
    ['0', '-180.01', /Longitude/],
  ])('rejects %s, %s', (lat, lon, message) => {
    expect(() => parseManualPoint(lat, lon)).toThrow(message);
  });
});

describe('buildDiscoverQuery', () => {
  it('sends the point, preset radius, activity, UTC window and open-only flag', () => {
    const query = buildDiscoverQuery(form({ radiusMiles: 25, openOnly: true, timeMode: 'around' }), { lat: 41.88, lon: -87.63 }, tuesdayNoon);
    const params = discoverSearchParams(query);
    expect(Object.fromEntries(params)).toEqual({
      lat: '41.88',
      lon: '-87.63',
      radiusMiles: '25',
      activity: 'basketball',
      fromUtc: new Date('2026-10-14T19:00:00').toISOString(),
      toUtc: new Date('2026-10-14T21:00:00').toISOString(),
      openOnly: 'true',
      pageSize: '20',
    });
  });

  it('passes the cursor for the next page', () => {
    expect(buildDiscoverQuery(form(), { lat: 0, lon: 0 }, tuesdayNoon, 'abc').cursor).toBe('abc');
  });
});

describe('currentPosition', () => {
  it('asks once (no tracking) and rounds the point', async () => {
    const getCurrentPosition = vi.fn((ok: PositionCallback) => ok({ coords: { latitude: 41.87811345, longitude: -87.62979912 } } as GeolocationPosition));
    const watchPosition = vi.fn();
    await expect(currentPosition({ getCurrentPosition, watchPosition } as unknown as Geolocation)).resolves.toEqual({ lat: 41.878113, lon: -87.629799 });
    expect(getCurrentPosition).toHaveBeenCalledTimes(1);
    expect(watchPosition).not.toHaveBeenCalled();
  });

  it('explains a denied permission', async () => {
    const getCurrentPosition = vi.fn((_ok: PositionCallback, fail: PositionErrorCallback) => fail({ code: 1 } as GeolocationPositionError));
    await expect(currentPosition({ getCurrentPosition } as unknown as Geolocation)).rejects.toThrow(/denied/);
  });

  it('works without geolocation support', async () => {
    await expect(currentPosition(undefined)).rejects.toThrow(/coordinates/);
  });
});

describe('result labels', () => {
  it('shows supply, required and open spots', () => {
    expect(spotsLabel(game().roster)).toBe('7/10 · 3 spots open');
    expect(spotsLabel({ ...game().roster, totalSupplyCount: 9, totalOpenQuantity: 1 })).toBe('9/10 · 1 spot open');
    expect(spotsLabel({ ...game().roster, totalSupplyCount: 10, totalOpenQuantity: 0, isRosterReady: true, isFull: true })).toBe('READY 10/10');
    expect(spotsLabel({ totalRequiredCount: 0, totalSupplyCount: 0, totalOpenQuantity: 0, isRosterReady: false, isFull: false })).toBe('No roster');
  });

  it("says when I'm playing or hosting", () => {
    expect(viewerLabel(game())).toBeNull();
    expect(viewerLabel(game({ viewer: { isHost: false, isParticipating: true, participationStatus: 2 } }))).toBe("You're playing");
    expect(viewerLabel(game({ viewer: { isHost: true, isParticipating: false } }))).toBe("You're hosting");
    expect(viewerLabel(game({ viewer: { isHost: true, isParticipating: true, participationStatus: 2 } }))).toBe("You're hosting and playing");
  });

  it('shows distance, approximate for a private venue', () => {
    expect(formatDistance(2.04, false)).toBe('2.0 mi');
    expect(formatDistance(23.6, false)).toBe('24 mi');
    expect(formatDistance(0.04, false)).toBe('< 0.1 mi');
    expect(formatDistance(3.2, true)).toBe('~3.2 mi');
  });

  it('shows the street only when the address is not masked', () => {
    expect(localityLabel(game())).toBe('1501 W Randolph St, Chicago, IL');
    expect(localityLabel(game({ venue: { ...game().venue, addressLine1: null, isAddressMasked: true, privacyLevel: 2 } }))).toBe('Chicago, IL');
  });

  it("shows the start in the venue's time zone", () => {
    expect(formatWhenInZone('2026-10-15T01:00:00Z', '2026-10-15T03:00:00Z', 'America/Chicago', 'en-US')).toBe('Wed, Oct 14 · 8:00 PM–10:00 PM CDT');
  });
});
