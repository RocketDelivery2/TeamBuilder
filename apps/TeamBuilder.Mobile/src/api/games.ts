import type { HttpClient } from './http';
import type { CreateEventRequest, CreateVenueRequest, DiscoveredOccurrencePage, DiscoverQuery, EventSummary, OccurrenceDetail, PlayerOccurrencePage, RosterAssignment, RosterSubscription, Venue } from './types';
const id = encodeURIComponent;
export class GameApi {
  constructor(private readonly http: HttpClient) {}
  async discover(query: DiscoverQuery, signal?: AbortSignal) {
    const params = new URLSearchParams();
    Object.entries(query).forEach(([key, value]) => { if (value !== undefined) params.set(key, String(value)); });
    return (await this.http.request<DiscoveredOccurrencePage>('GET', '/api/v1/discover/occurrences?' + params, undefined, signal)).data;
  }
  async mine(options: { cursor?: string; fromUtc?: string; toUtc?: string; includeTerminal?: boolean } = {}, signal?: AbortSignal) {
    const params = new URLSearchParams({ includeHosted: 'true', pageSize: '20' });
    Object.entries(options).forEach(([key, value]) => { if (value !== undefined) params.set(key, String(value)); });
    return (await this.http.request<PlayerOccurrencePage>('GET', '/api/v1/players/me/occurrences?' + params, undefined, signal)).data;
  }
  async detail(occurrenceId: string, signal?: AbortSignal) {
    return (await this.http.request<OccurrenceDetail>('GET', '/api/v1/events/' + id(occurrenceId) + '/detail', undefined, signal)).data;
  }
  async create(body: CreateEventRequest) { return (await this.http.request<EventSummary>('POST', '/api/v1/events', body)).data; }
  async createVenue(body: CreateVenueRequest) { return (await this.http.request<Venue>('POST', '/api/v1/venues', body)).data; }
  claim(game: string, requirementId: string) { return this.http.request<RosterAssignment>('POST', '/api/v1/events/' + id(game) + '/roster/claims', { requirementId }); }
  async leave(game: string, assignment: string) { return (await this.http.request<RosterAssignment>('POST', '/api/v1/events/' + id(game) + '/roster/assignments/' + id(assignment) + '/leave')).data; }
  async subscription(game: string, requirement: string, enabled: boolean) {
    return (await this.http.request<RosterSubscription | undefined>(enabled ? 'PUT' : 'DELETE',
      '/api/v1/events/' + id(game) + '/roster/requirements/' + id(requirement) + '/subscription')).data;
  }
}
