import type { DiscoveredOccurrence, DiscoverQuery } from '../api/types';

/** The radius presets offered in the UI; the API accepts any radius up to 100 miles. */
export const RADIUS_PRESETS_MILES = [15, 25, 50] as const;
export type RadiusPreset = (typeof RADIUS_PRESETS_MILES)[number];

/** When to search on the chosen day, in the searcher's own time zone. */
export type TimeMode = 'any' | 'evening' | 'around';

export interface DiscoverForm {
  activity: string;
  /** Local calendar date, yyyy-mm-dd. */
  date: string;
  timeMode: TimeMode;
  /** Local HH:mm, used with timeMode 'around'. */
  aroundTime: string;
  radiusMiles: RadiusPreset;
  openOnly: boolean;
  /** Manual coordinates as typed (QA/dev fallback), kept in memory only. */
  manualLat: string;
  manualLon: string;
}

export interface SearchPoint {
  lat: number;
  lon: number;
}

/** Evening is 5 PM to 11 PM; "around" is an hour either side of the chosen time. */
export const EVENING_START_HOUR = 17;
export const EVENING_END_HOUR = 23;
export const AROUND_MINUTES = 60;

/** Today as a local yyyy-mm-dd date. */
export function localDate(now: Date = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

export function initialDiscoverForm(now: Date = new Date()): DiscoverForm {
  return {
    activity: 'basketball',
    date: localDate(now),
    timeMode: 'any',
    aroundTime: '20:00',
    radiusMiles: 15,
    openOnly: false,
    manualLat: '',
    manualLon: '',
  };
}

/**
 * The UTC window for the human intent "this day", "this evening" or "around 8 PM", taken in
 * the browser's time zone. The API never reinterprets it; a window that has already started
 * begins now instead, so past games are not offered.
 */
export function searchWindow(form: Pick<DiscoverForm, 'date' | 'timeMode' | 'aroundTime'>, now: Date = new Date()): { fromUtc: string; toUtc: string } {
  const day = new Date(`${form.date}T00:00:00`);
  if (Number.isNaN(day.getTime())) throw new Error('Choose a valid date.');

  let from: Date;
  let to: Date;
  if (form.timeMode === 'evening') {
    from = new Date(`${form.date}T${pad(EVENING_START_HOUR)}:00:00`);
    to = new Date(`${form.date}T${pad(EVENING_END_HOUR)}:00:00`);
  } else if (form.timeMode === 'around') {
    const at = new Date(`${form.date}T${form.aroundTime}:00`);
    if (Number.isNaN(at.getTime())) throw new Error('Choose a valid time.');
    from = new Date(at.getTime() - AROUND_MINUTES * 60_000);
    to = new Date(at.getTime() + AROUND_MINUTES * 60_000);
  } else {
    from = day;
    // The next local midnight (23 or 25 hours away on a DST change day).
    to = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1);
  }

  if (to.getTime() <= now.getTime()) throw new Error('That time has already passed. Pick a later date or time.');
  if (from.getTime() < now.getTime()) from = now;
  return { fromUtc: from.toISOString(), toUtc: to.toISOString() };
}

/** Validates manually typed coordinates. */
export function parseManualPoint(latText: string, lonText: string): SearchPoint {
  const lat = Number(latText.trim());
  const lon = Number(lonText.trim());
  if (latText.trim() === '' || !Number.isFinite(lat) || lat < -90 || lat > 90) throw new Error('Latitude must be a number between -90 and 90.');
  if (lonText.trim() === '' || !Number.isFinite(lon) || lon < -180 || lon > 180) throw new Error('Longitude must be a number between -180 and 180.');
  return { lat, lon };
}

export function buildDiscoverQuery(form: DiscoverForm, point: SearchPoint, now: Date = new Date(), cursor?: string): DiscoverQuery {
  const { fromUtc, toUtc } = searchWindow(form, now);
  return {
    lat: point.lat,
    lon: point.lon,
    radiusMiles: form.radiusMiles,
    activity: form.activity || undefined,
    fromUtc,
    toUtc,
    openOnly: form.openOnly,
    pageSize: 20,
    ...(cursor ? { cursor } : {}),
  };
}

export function formatDistance(miles: number | null | undefined, approximate: boolean): string {
  if (miles == null) return '';
  const value = miles < 0.1 ? '< 0.1' : miles < 10 ? miles.toFixed(1) : Math.round(miles).toString();
  return `${approximate ? '~' : ''}${value} mi`;
}

/** "7/10 · 3 spots open", "READY 10/10" or "No roster". */
export function spotsLabel(roster: DiscoveredOccurrence['roster']): string {
  const { totalSupplyCount: supply, totalRequiredCount: required, totalOpenQuantity: open, isRosterReady } = roster;
  if (required === 0) return 'No roster';
  if (isRosterReady) return `READY ${supply}/${required}`;
  return `${supply}/${required} · ${open} ${open === 1 ? 'spot' : 'spots'} open`;
}

/** "You're hosting", "You're playing" or nothing. */
export function viewerLabel(game: DiscoveredOccurrence): string | null {
  if (game.viewer?.isHost && game.viewer.isParticipating) return "You're hosting and playing";
  if (game.viewer?.isHost) return "You're hosting";
  if (game.viewer?.isParticipating) return "You're playing";
  return null;
}

/** The venue's locality line: city and state, never more for a masked venue. */
export function localityLabel(game: DiscoveredOccurrence): string {
  const { venue } = game;
  const locality = [venue.city, venue.stateOrProvince].filter(Boolean).join(', ');
  if (!venue.isAddressMasked && venue.addressLine1) return [venue.addressLine1, locality].filter(Boolean).join(', ');
  return locality;
}

/** Start (and end) shown in the game's own time zone when it has one. */
export function formatWhenInZone(startUtc: string, endUtc?: string | null, timeZone?: string | null, locale?: string): string {
  const zone = timeZone ?? undefined;
  const start = new Date(startUtc);
  const day = start.toLocaleDateString(locale, { weekday: 'short', month: 'short', day: 'numeric', timeZone: zone });
  const time = (d: Date) => d.toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit', timeZone: zone });
  const zoneName = zone ? ` ${shortZoneName(start, zone, locale)}` : '';
  if (!endUtc) return `${day} · ${time(start)}${zoneName}`;
  return `${day} · ${time(start)}–${time(new Date(endUtc))}${zoneName}`;
}

function shortZoneName(at: Date, timeZone: string, locale?: string): string {
  const part = new Intl.DateTimeFormat(locale, { timeZone, timeZoneName: 'short' }).formatToParts(at).find((p) => p.type === 'timeZoneName');
  return part?.value ?? '';
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** The browser's IANA time zone, for new venues created from this device. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * One-shot browser location, only ever called from an explicit "Use my location" tap. No
 * watchPosition, nothing stored: the point lives in component state for the next search.
 */
export function currentPosition(geolocation: Geolocation | undefined = typeof navigator === 'undefined' ? undefined : navigator.geolocation): Promise<SearchPoint> {
  return new Promise((resolve, reject) => {
    if (!geolocation) {
      reject(new Error('This browser cannot share its location. Enter coordinates instead.'));
      return;
    }
    geolocation.getCurrentPosition(
      (position) => resolve({ lat: round6(position.coords.latitude), lon: round6(position.coords.longitude) }),
      (error) =>
        reject(new Error(error.code === 1 ? 'Location permission was denied. Enter coordinates instead.' : 'Could not get your location. Enter coordinates instead.')),
      { enableHighAccuracy: false, maximumAge: 60_000, timeout: 15_000 },
    );
  });
}

function round6(n: number): number {
  return Math.round(n * 1e6) / 1e6;
}
