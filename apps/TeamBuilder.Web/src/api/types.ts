// Wire contracts of the TeamBuilder API used by this client. Enums arrive as their numeric
// values (the API has no string enum converter); the name maps below are display-only.

export const EventStatus = {
  Planned: 1,
  Open: 2,
  InProgress: 3,
  Completed: 4,
  Cancelled: 5,
  Archived: 6,
} as const;
export type EventStatus = (typeof EventStatus)[keyof typeof EventStatus];

export const AssignmentStatus = {
  Reserved: 1,
  Confirmed: 2,
  CheckedIn: 3,
  Active: 4,
  Departed: 5,
  NoShow: 6,
  Cancelled: 7,
} as const;
export type AssignmentStatus = (typeof AssignmentStatus)[keyof typeof AssignmentStatus];

export const eventStatusLabel: Record<number, string> = {
  1: 'Planned',
  2: 'Open',
  3: 'In progress',
  4: 'Completed',
  5: 'Cancelled',
  6: 'Archived',
};

export const assignmentStatusLabel: Record<number, string> = {
  1: 'Reserved',
  2: 'Confirmed',
  3: 'Checked in',
  4: 'Playing',
  5: 'Left',
  6: 'No-show',
  7: 'Cancelled',
};

export interface PlayerProfile {
  id: string;
  username: string;
  displayName?: string | null;
}

export interface PublicPlayer {
  id: string;
  username: string;
  displayName?: string | null;
}

export interface RosterRequirement {
  id: string;
  occurrenceId: string;
  roleCode: string;
  displayPosition?: string | null;
  requiredCount: number;
  supplyCount: number;
  openQuantity: number;
}

export interface RosterAssignment {
  id: string;
  occurrenceId: string;
  playerId: string;
  username: string;
  displayName?: string | null;
  requirementId?: string | null;
  roleCode?: string | null;
  status: AssignmentStatus;
}

export interface OccurrenceParticipant {
  assignmentId: string;
  playerId: string;
  username: string;
  displayName?: string | null;
  requirementId?: string | null;
  roleCode?: string | null;
  status: AssignmentStatus;
  isHost: boolean;
}

/** GET /api/v1/events/{id}/detail */
export interface OccurrenceDetail {
  occurrenceId: string;
  seriesId?: string | null;
  teamId?: string | null;
  name: string;
  description?: string | null;
  scheduledStartUtc: string;
  scheduledEndUtc?: string | null;
  status: EventStatus;
  acceptsRosterChanges: boolean;
  category?: string | null;
  region?: string | null;
  venueId?: string | null;
  location?: string | null;
  /** The attached venue, masked unless the caller hosts or plays in the game (Private venues). */
  venue?: OccurrenceVenue | null;
  hostPlayerId?: string | null;
  hostUsername?: string | null;
  hostDisplayName?: string | null;
  requiredCount: number;
  supplyCount: number;
  openQuantity: number;
  isRosterReady: boolean;
  requirements: RosterRequirement[];
  participants: OccurrenceParticipant[];
  isHost: boolean;
  myAssignmentId?: string | null;
  myAssignmentStatus?: AssignmentStatus | null;
  myRequirementId?: string | null;
  /** Requirements the caller asked to hear about ("Notify me if a spot opens"); the caller's own only. */
  mySubscribedRequirementIds?: string[];
}

/** PUT /api/v1/events/{id}/roster/requirements/{requirementId}/subscription */
export interface RosterSubscription {
  occurrenceId: string;
  rosterRequirementId: string;
  subscribed: boolean;
  createdAtUtc?: string | null;
}

/** One of the caller's in-app notifications. `occurrenceId` is the link; title/body are display text. */
export interface InAppNotification {
  id: string;
  type: string;
  occurrenceId: string;
  rosterRequirementId?: string | null;
  title: string;
  body: string;
  createdAtUtc: string;
  readAtUtc?: string | null;
  isRead: boolean;
}

export interface InAppNotificationPage {
  items: InAppNotification[];
  nextCursor?: string | null;
}

/** GET /api/v1/push/config: whether this server sends browser alerts, and the key to subscribe with. */
export interface WebPushConfig {
  enabled: boolean;
  vapidPublicKey?: string | null;
}

/** The browser's PushSubscription.toJSON(), plus the endpoint it replaces. Sent, never returned. */
export interface RegisterPushSubscription {
  endpoint: string;
  expirationTime?: number | null;
  keys: { p256dh: string; auth: string };
  previousEndpoint?: string;
}

/** One of the caller's registered browsers (no endpoint or keys). */
export interface PushDevice {
  id: string;
  userAgentFamily?: string | null;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  expiresAtUtc?: string | null;
  isActive: boolean;
  disabledAtUtc?: string | null;
  disabledReason?: string | null;
}

/** One item of GET /api/v1/players/me/occurrences */
export interface PlayerOccurrence {
  occurrenceId: string;
  name: string;
  scheduledStartUtc: string;
  scheduledEndUtc?: string | null;
  status: EventStatus;
  location?: string | null;
  category?: string | null;
  hostPlayerId?: string | null;
  hostUsername?: string | null;
  hostDisplayName?: string | null;
  isHost: boolean;
  myAssignmentId?: string | null;
  myAssignmentStatus?: AssignmentStatus | null;
  isRosterReady: boolean;
  totalRequiredCount: number;
  totalSupplyCount: number;
  totalOpenQuantity: number;
}

export interface PlayerOccurrencePage {
  items: PlayerOccurrence[];
  nextCursor?: string | null;
}

export interface CreateRosterRequirementRequest {
  roleCode?: string;
  requiredCount: number;
}

/** POST /api/v1/events (additive roster fields included). */
export interface CreateEventRequest {
  name: string;
  description?: string;
  eventDateUtc: string;
  scheduledEndUtc?: string;
  category?: string;
  location?: string;
  rosterRequirements?: CreateRosterRequirementRequest[];
  hostParticipates?: boolean;
  /** A venue from POST /api/v1/venues; games with a physical venue appear in Discover. */
  venueId?: string;
}

export interface EventSummary {
  id: string;
  name: string;
  hostId?: string | null;
}

export const VenueType = { Indoor: 1, Outdoor: 2, Virtual: 3 } as const;
export type VenueType = (typeof VenueType)[keyof typeof VenueType];

/** Public: the address may be shown. Private: only players in the game see the exact address. */
export const VenuePrivacyLevel = { Public: 1, Private: 2 } as const;
export type VenuePrivacyLevel = (typeof VenuePrivacyLevel)[keyof typeof VenuePrivacyLevel];

/** POST /api/v1/venues */
export interface CreateVenueRequest {
  name: string;
  addressLine1?: string;
  city?: string;
  stateOrProvince?: string;
  postalCode?: string;
  countryCode?: string;
  latitude: number;
  longitude: number;
  timeZoneId: string;
  venueType: VenueType;
  privacyLevel: VenuePrivacyLevel;
}

export interface Venue {
  id: string;
  name: string;
  city?: string | null;
  stateOrProvince?: string | null;
  isAddressMasked: boolean;
}

/** A venue on a discovery result or game detail, after privacy masking. */
export interface OccurrenceVenue {
  venueId: string;
  name: string;
  city?: string | null;
  stateOrProvince?: string | null;
  countryCode?: string | null;
  addressLine1?: string | null;
  addressLine2?: string | null;
  postalCode?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  timeZoneId?: string | null;
  privacyLevel: VenuePrivacyLevel;
  distanceMiles?: number | null;
  isAddressMasked: boolean;
}

/** One item of GET /api/v1/discover/occurrences */
export interface DiscoveredOccurrence {
  occurrenceId: string;
  seriesId?: string | null;
  name: string;
  category?: string | null;
  status: EventStatus;
  scheduledStartUtc: string;
  scheduledEndUtc?: string | null;
  timeZoneId?: string | null;
  venue: OccurrenceVenue;
  roster: {
    totalRequiredCount: number;
    totalSupplyCount: number;
    totalOpenQuantity: number;
    isRosterReady: boolean;
    isFull: boolean;
  };
  viewer?: {
    isHost: boolean;
    isParticipating: boolean;
    participationStatus?: AssignmentStatus | null;
  } | null;
}

export interface DiscoveredOccurrencePage {
  items: DiscoveredOccurrence[];
  nextCursor?: string | null;
}

/** Query of GET /api/v1/discover/occurrences. The point is sent for this search only. */
export interface DiscoverQuery {
  lat: number;
  lon: number;
  radiusMiles: number;
  activity?: string;
  fromUtc: string;
  toUtc: string;
  openOnly: boolean;
  pageSize?: number;
  cursor?: string;
}

/** RFC 7807 problem details as the API returns them, with the stable roster `code`. */
export interface ProblemDetails {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
}
