import type { CreateEventRequest } from '../api/types';

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
  };
}

/**
 * Builds the atomic create request: the occurrence plus one generic `participant`
 * requirement of `requiredPlayers`, and whether the host takes a spot. The local date and
 * time are interpreted in the browser's time zone and sent as UTC instants.
 */
export function buildCreateEventRequest(form: CreateGameForm): CreateEventRequest {
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
  };
}
