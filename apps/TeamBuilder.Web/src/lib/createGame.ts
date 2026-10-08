import { VenuePrivacyLevel, VenueType, type CreateEventRequest, type CreateVenueRequest } from '../api/types';
import { parseManualPoint } from './discover';

/** Form state of the Create Pickup Game screen. */
export interface CreateGameForm {
  activity: string;
  title: string;
  /** Local calendar date, yyyy-mm-dd. */
  date: string;
  /** Local start time, HH:mm. */
  startTime: string;
  durationMinutes: number;
  location: string;
  requiredPlayers: number;
  hostIsPlaying: boolean;
  /** Attach a real venue (with coordinates) so the game shows up in Discover. */
  useVenue: boolean;
  venueName: string;
  venueAddress: string;
  venueCity: string;
  venueState: string;
  venueType: 'outdoor' | 'indoor';
  venuePrivacy: 'public' | 'private';
  /** As typed, or filled from one "Use current location" tap. */
  venueLat: string;
  venueLon: string;
}

export interface ActivityPreset {
  value: string;
  label: string;
  /** UI convenience only; the API has no activity-specific defaults. */
  defaultRequiredPlayers: number;
}

export const ACTIVITY_PRESETS: ActivityPreset[] = [
  { value: 'basketball', label: 'Basketball', defaultRequiredPlayers: 10 },
  { value: 'soccer', label: 'Soccer', defaultRequiredPlayers: 14 },
  { value: 'volleyball', label: 'Volleyball', defaultRequiredPlayers: 12 },
  { value: 'other', label: 'Other', defaultRequiredPlayers: 8 },
];

export function defaultRequiredPlayersFor(activity: string): number {
  return ACTIVITY_PRESETS.find((preset) => preset.value === activity)?.defaultRequiredPlayers ?? 8;
}

/** The next Wednesday (today if it is Wednesday) as a local yyyy-mm-dd date. */
/** The next Wednesday whose `startHour` (local time) is still ahead: today only before then. */
export function nextWednesday(now: Date = new Date(), startHour = 20): string {
  const date = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  let days = (3 - date.getDay() + 7) % 7;
  if (days === 0 && now.getHours() >= startHour) days = 7;
  date.setDate(date.getDate() + days);
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

export function initialCreateGameForm(now: Date = new Date()): CreateGameForm {
  return {
    activity: 'basketball',
    title: 'Wednesday Night Hoops',
    date: nextWednesday(now),
    startTime: '20:00',
    durationMinutes: 120,
    location: '',
    requiredPlayers: defaultRequiredPlayersFor('basketball'),
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
  };
}

/**
 * Builds the atomic create request: the occurrence plus one generic `participant`
 * requirement of `requiredPlayers`, and whether the host takes a spot. The local date and
 * time are interpreted in the browser's time zone and sent as UTC instants.
 */
export function buildCreateEventRequest(form: CreateGameForm, venueId?: string): CreateEventRequest {
  const start = new Date(`${form.date}T${form.startTime}:00`);
  if (Number.isNaN(start.getTime())) throw new Error('Choose a valid date and start time.');
  if (!Number.isInteger(form.requiredPlayers) || form.requiredPlayers < 1) {
    throw new Error('Required players must be at least 1.');
  }
  if (!Number.isFinite(form.durationMinutes) || form.durationMinutes <= 0) {
    throw new Error('Duration must be positive.');
  }

  const end = new Date(start.getTime() + form.durationMinutes * 60_000);
  const location = form.location.trim();

  return {
    name: form.title.trim(),
    category: form.activity,
    eventDateUtc: start.toISOString(),
    scheduledEndUtc: end.toISOString(),
    ...(location ? { location } : {}),
    rosterRequirements: [{ roleCode: 'participant', requiredCount: form.requiredPlayers }],
    hostParticipates: form.hostIsPlaying,
    ...(venueId ? { venueId } : {}),
  };
}

/**
 * The venue to create first when the host attached one, else null. Coordinates come from the
 * device or are typed: nothing is geocoded from the address. The time zone is the browser's,
 * since the host is normally creating the game where it is played.
 */
export function buildCreateVenueRequest(form: CreateGameForm, timeZoneId: string): CreateVenueRequest | null {
  if (!form.useVenue) return null;
  const name = form.venueName.trim();
  if (!name) throw new Error('Give the venue a name.');
  const { lat, lon } = parseManualPoint(form.venueLat, form.venueLon);
  const optional = (key: 'addressLine1' | 'city' | 'stateOrProvince', value: string) => (value.trim() ? { [key]: value.trim() } : {});
  return {
    name,
    ...optional('addressLine1', form.venueAddress),
    ...optional('city', form.venueCity),
    ...optional('stateOrProvince', form.venueState),
    latitude: lat,
    longitude: lon,
    timeZoneId,
    venueType: form.venueType === 'indoor' ? VenueType.Indoor : VenueType.Outdoor,
    privacyLevel: form.venuePrivacy === 'private' ? VenuePrivacyLevel.Private : VenuePrivacyLevel.Public,
  };
}
