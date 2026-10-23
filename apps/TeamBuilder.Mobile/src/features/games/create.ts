import { Temporal } from '@js-temporal/polyfill';
import type { CreateEventRequest, CreateVenueRequest } from '../../api/types';
import { point, wallTimeUtc } from '../../utils/time';
export interface GameForm {
  name: string; description: string; category: string; date: string; time: string; timeZone: string;
  duration: string; quantity: string; hostParticipates: boolean; venueEnabled: boolean;
  venueName: string; address: string; city: string; latitude: string; longitude: string; privateVenue: boolean; indoor: boolean;
}
export function buildGame(form: GameForm, venueId?: string): CreateEventRequest {
  const name = form.name.trim(), quantity = Number(form.quantity), duration = Number(form.duration);
  if (!name || name.length > 200) throw new Error('Give the game a name of at most 200 characters.');
  if (!form.category.trim() || form.category.length > 100) throw new Error('Enter an activity of at most 100 characters.');
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > 100000) throw new Error('Roster quantity must be a whole number from 1 to 100000.');
  if (!Number.isInteger(duration) || duration < 1 || duration > 10080) throw new Error('Duration must be 1 to 10080 minutes.');
  const start = wallTimeUtc(form.date, form.time, form.timeZone);
  return { name, description: form.description.trim() || undefined, category: form.category.trim(),
    eventDateUtc: start, scheduledEndUtc: Temporal.Instant.from(start).add({ minutes: duration }).toString(),
    rosterRequirements: [{ roleCode: 'participant', requiredCount: quantity }], hostParticipates: form.hostParticipates, venueId };
}
export function buildVenue(form: GameForm): CreateVenueRequest | null {
  if (!form.venueEnabled) return null;
  if (!form.venueName.trim()) throw new Error('Give the venue a name.');
  const { lat, lon } = point(form.latitude, form.longitude);
  return { name: form.venueName.trim(), addressLine1: form.address.trim() || undefined, city: form.city.trim() || undefined,
    latitude: lat, longitude: lon, timeZoneId: form.timeZone, venueType: form.indoor ? 1 : 2, privacyLevel: form.privateVenue ? 2 : 1 };
}
