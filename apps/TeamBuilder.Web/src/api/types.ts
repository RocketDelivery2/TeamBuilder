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
}

export interface EventSummary {
  id: string;
  name: string;
  hostId?: string | null;
}

/** RFC 7807 problem details as the API returns them, with the stable roster `code`. */
export interface ProblemDetails {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
}
