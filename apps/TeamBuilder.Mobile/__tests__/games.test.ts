import { buildGame, buildVenue, type GameForm } from '../src/features/games/create';
import { point, searchWindow, wallTimeUtc } from '../src/utils/time';
const form: GameForm = { name: 'Wednesday Basketball', description: '', category: 'basketball', date: '2026-10-28', time: '20:00', timeZone: 'America/New_York',
  duration: '120', quantity: '10', hostParticipates: true, venueEnabled: true, venueName: 'QA court', address: 'Synthetic address', city: 'QA city',
  latitude: '40', longitude: '-74', privateVenue: true, indoor: false };
test('creates a standalone ten-person roster atomically with host participation', () => {
  expect(buildGame(form, 'venue-id')).toMatchObject({ eventDateUtc: '2026-10-29T00:00:00Z', scheduledEndUtc: '2026-10-29T02:00:00Z',
    rosterRequirements: [{ roleCode: 'participant', requiredCount: 10 }], hostParticipates: true, venueId: 'venue-id' });
  expect(buildGame(form)).not.toHaveProperty('teamId');
  expect(buildVenue(form)).toMatchObject({ latitude: 40, longitude: -74, privacyLevel: 2, timeZoneId: 'America/New_York' });
});
test.each(['0', '-1', '2.5', 'no'])('rejects invalid roster quantity %s', quantity => {
  expect(() => buildGame({ ...form, quantity })).toThrow();
});
test('rejects DST gaps and ambiguous wall times', () => {
  expect(() => wallTimeUtc('2026-03-08', '02:30', 'America/New_York')).toThrow(/daylight saving/);
  expect(() => wallTimeUtc('2026-11-01', '01:30', 'America/New_York')).toThrow(/daylight saving/);
  expect(wallTimeUtc('2026-11-01', '03:30', 'America/New_York')).toBe('2026-11-01T08:30:00Z');
});
test('a spring DST search day spans 23 hours rather than assuming 24', () => {
  const window = searchWindow('2026-03-08', undefined, 'America/New_York');
  expect(Date.parse(window.toUtc) - Date.parse(window.fromUtc)).toBe(23 * 3600000);
});
test.each([['', '0'], ['91', '0'], ['0', '181'], ['Infinity', '0']])('rejects invalid manual coordinates', (lat, lon) => {
  expect(() => point(lat, lon)).toThrow();
});
