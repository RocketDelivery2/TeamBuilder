import type { HttpClient } from './http';
// Source: TeamBuilder.Application/DTOs/PlayerDto.cs and PlayersController.
export interface Player {
  id: string; username: string; displayName?: string | null; bio?: string | null;
  email?: string | null; region?: string | null; avatarUrl?: string | null;
  createdAtUtc: string; updatedAtUtc?: string | null;
}
export class PlayerApi {
  constructor(private readonly http: HttpClient) {}
  async me(signal?: AbortSignal) { return (await this.http.request<Player>('GET', '/api/v1/players/me', undefined, signal)).data; }
  async onboard(username: string, displayName: string) {
    return (await this.http.request<Player>('POST', '/api/v1/players/me', { username, displayName })).data;
  }
  async update(id: string, fields: Pick<Player, 'displayName' | 'bio' | 'region'>) {
    return (await this.http.request<Player>('PUT', '/api/v1/players/' + encodeURIComponent(id), fields)).data;
  }
}
