import { Temporal } from '@js-temporal/polyfill';
export const deviceZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
export const today = () => Temporal.Now.plainDateISO().toString();
export function wallTimeUtc(date: string, time: string, zone: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !/^\d{2}:\d{2}$/.test(time)) throw new Error('Choose a valid date and time.');
  try {
    return Temporal.PlainDateTime.from(date + 'T' + time).toZonedDateTime(zone, { disambiguation: 'reject' }).toInstant().toString();
  } catch { throw new Error('This local time is invalid or occurs twice because of daylight saving. Choose another time or time zone.'); }
}
export function searchWindow(date: string, time: string | undefined, zone: string) {
  const fromUtc = wallTimeUtc(date, time ?? '00:00', zone);
  const toUtc = time ? Temporal.Instant.from(fromUtc).add({ hours: 2 }).toString() :
    wallTimeUtc(Temporal.PlainDate.from(date).add({ days: 1 }).toString(), '00:00', zone);
  return { fromUtc, toUtc };
}
export function displayTime(value: string, zone?: string | null): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone: zone || undefined }).format(new Date(value));
}
export function point(latitude: string, longitude: string) {
  const lat = Number(latitude), lon = Number(longitude);
  if (!latitude.trim() || !longitude.trim() || !Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) {
    throw new Error('Enter a latitude from -90 to 90 and longitude from -180 to 180.');
  }
  return { lat, lon };
}
