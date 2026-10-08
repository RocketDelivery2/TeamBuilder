import { HttpClient, type ApiResponse } from './http';
import type {
  CreateEventRequest,
  CreateVenueRequest,
  DiscoveredOccurrencePage,
  DiscoverQuery,
  Venue,
  EventStatus,
  EventSummary,
  OccurrenceDetail,
  PlayerOccurrencePage,
  PlayerProfile,
  PublicPlayer,
  RosterAssignment,
} from './types';

export type HostAction = 'check-in' | 'activate' | 'no-show' | 'remove';

/** Typed endpoints of the TeamBuilder API used by the QA client. No business rules here. */
export class TeamBuilderApi {
  constructor(private readonly http: HttpClient) {}

  async getMe(): Promise<PlayerProfile> {
    return (await this.http.request<PlayerProfile>('GET', '/api/v1/players/me')).data;
  }

  async onboard(username: string, displayName?: string): Promise<PlayerProfile> {
    return (await this.http.request<PlayerProfile>('POST', '/api/v1/players/me', { username, displayName })).data;
  }

  async findPlayerByUsername(username: string): Promise<PublicPlayer> {
    return (await this.http.request<PublicPlayer>('GET', `/api/v1/players/username/${encodeURIComponent(username)}`)).data;
  }

  async myGames(includeHosted = true, cursor?: string): Promise<PlayerOccurrencePage> {
    const query = new URLSearchParams({ includeHosted: String(includeHosted) });
    if (cursor) query.set('cursor', cursor);
    return (await this.http.request<PlayerOccurrencePage>('GET', `/api/v1/players/me/occurrences?${query}`)).data;
  }

  async createGame(request: CreateEventRequest): Promise<EventSummary> {
    return (await this.http.request<EventSummary>('POST', '/api/v1/events', request)).data;
  }

  async createVenue(request: CreateVenueRequest): Promise<Venue> {
    return (await this.http.request<Venue>('POST', '/api/v1/venues', request)).data;
  }

  /** Nearby games, closest first. The search point goes in this one request and nowhere else. */
  async discover(query: DiscoverQuery): Promise<DiscoveredOccurrencePage> {
    return (await this.http.request<DiscoveredOccurrencePage>('GET', `/api/v1/discover/occurrences?${discoverSearchParams(query)}`)).data;
  }

  async getDetail(occurrenceId: string): Promise<OccurrenceDetail> {
    return (await this.http.request<OccurrenceDetail>('GET', `/api/v1/events/${occurrenceId}/detail`)).data;
  }

  /** 201 = joined, 200 = the caller's existing claim (safe retry). */
  claim(occurrenceId: string, requirementId: string): Promise<ApiResponse<RosterAssignment>> {
    return this.http.request<RosterAssignment>('POST', `/api/v1/events/${occurrenceId}/roster/claims`, { requirementId });
  }

  async leave(occurrenceId: string, assignmentId: string): Promise<RosterAssignment> {
    return (await this.http.request<RosterAssignment>('POST', `/api/v1/events/${occurrenceId}/roster/assignments/${assignmentId}/leave`)).data;
  }

  async hostAction(occurrenceId: string, assignmentId: string, action: HostAction): Promise<RosterAssignment> {
    return (await this.http.request<RosterAssignment>('POST', `/api/v1/events/${occurrenceId}/roster/assignments/${assignmentId}/${action}`)).data;
  }

  /** Host-only status change through the existing PUT (e.g. start play, finish the game). */
  async setStatus(occurrenceId: string, status: EventStatus): Promise<EventSummary> {
    return (await this.http.request<EventSummary>('PUT', `/api/v1/events/${occurrenceId}`, { status })).data;
  }

  async transferHost(occurrenceId: string, newHostPlayerId: string): Promise<EventSummary> {
    return (await this.http.request<EventSummary>('POST', `/api/v1/events/${occurrenceId}/host/transfer`, { newHostPlayerId })).data;
  }
}

export function discoverSearchParams(query: DiscoverQuery): URLSearchParams {
  const params = new URLSearchParams({
    lat: String(query.lat),
    lon: String(query.lon),
    radiusMiles: String(query.radiusMiles),
    fromUtc: query.fromUtc,
    toUtc: query.toUtc,
    openOnly: String(query.openOnly),
  });
  if (query.activity) params.set('activity', query.activity);
  if (query.pageSize) params.set('pageSize', String(query.pageSize));
  if (query.cursor) params.set('cursor', query.cursor);
  return params;
}
