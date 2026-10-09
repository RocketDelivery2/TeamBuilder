# TeamBuilder API

## Overview

TeamBuilder is an API-first, frontend-agnostic platform for building, hosting,
joining, maintaining, and refilling teams. The ASP.NET Core Web API is the
middle layer between any client-side frontend and the backend data platform.

- **Base path:** `api/v1`
- **Format:** JSON (request and response)
- **Authentication:** JWT bearer auth is required on protected routes; public reads remain available. Player onboarding and profile writes require authentication.
  See [Authentication](#authentication) for details and local dev token setup.
- **Persistence:** EF Core Code First targeting Azure SQL Server.
- **Health endpoints:** `GET /healthz/live` (liveness), `GET /healthz/ready` (readiness); `/health` and `/health/ready` remain as aliases

---

## Architecture Summary

```text
Client
  └── TeamBuilder.Api (ASP.NET Core Web API)
        ├── Controllers (thin, no business logic)
        ├── TeamBuilder.Application
        │     ├── Interfaces (ITeamService, IPlayerService, …)
        │     ├── DTOs (request / response contracts)
        │     └── Models (PaginatedResult<T>)
        └── TeamBuilder.Infrastructure
              ├── Services (EF Core implementations)
              └── Data (TeamBuilderDbContext, EF configurations)
```

---

## Running the API Locally

### Prerequisites

- .NET 10 SDK
- SQL Server or SQL Server LocalDB
- (Optional) Visual Studio 2026 or VS Code

### Configuration

Create or update `src/TeamBuilder.Api/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=(localdb)\\mssqllocaldb;Database=TeamBuilder;Trusted_Connection=True;"
  },
  "AllowedOrigins": "*"
}
```

Do **not** commit real connection strings or secrets.

### Start

```bash
cd src/TeamBuilder.Api
dotnet run
```

### Swagger UI

Available at `https://localhost:<port>/swagger` in the Development environment.
The port is shown in the terminal when the API starts.

### Health Check

```bash
# Liveness: is the process running?
curl https://localhost:<port>/healthz/live

# Readiness: is the database reachable and fully migrated?
curl https://localhost:<port>/healthz/ready
```

---

## Pagination

All list endpoints return a `PaginatedResult<T>` envelope:

```json
{
  "items": [],
  "totalCount": 0,
  "page": 1,
  "pageSize": 20,
  "totalPages": 0,
  "hasPreviousPage": false,
  "hasNextPage": false
}
```

**Query parameters (all list endpoints):**

| Parameter  | Default | Max | Description                       |
|------------|---------|-----|-----------------------------------|
| `page`     | `1`     | —   | Values below 1 are clamped to 1.  |
| `pageSize` | `20`    | `100` | Values outside 1–100 reset to 20. |

---

## Correlation ID

Every response includes an `X-Request-Id` header that can be used to correlate
client requests with server-side log entries.

| Scenario | Behaviour |
|---|---|
| Request includes `X-Request-Id` | The value is echoed back in the response header unchanged. |
| Request omits `X-Request-Id` | A new ID is generated from the ASP.NET Core `TraceIdentifier` and added to the response. |

Using a client-supplied value is useful when tracing end-to-end requests across
multiple services. The server **never** logs authorization headers, cookies, or
request/response bodies.

```http
GET /api/v1/teams HTTP/1.1
X-Request-Id: my-client-trace-001
```

```http
HTTP/1.1 200 OK
X-Request-Id: my-client-trace-001
```

---

## Authentication

JWT bearer validation is implemented. `ExternalIdentity` is the default
authentication and challenge scheme. A validated token's exact issuer plus the
configured subject claim identifies a `PlayerIdentity`, which maps to the
internal TeamBuilder `Player.Id`. Issuer and subject are opaque, exact keys:
they are not trimmed, case-folded, URI-normalized, or otherwise rewritten.
Subjects do not need to be GUIDs. Authentication is endpoint-specific; see
[`docs/auth-plan.md`](auth-plan.md) for the current identity model and
[`docs/oidc-rollout.md`](oidc-rollout.md) for provider configuration guidance.

### Protected endpoints

Protected endpoints require a valid bearer token. Requests without a valid
token receive `401 Unauthorized`. A valid external identity without a linked
TeamBuilder player receives `403 Forbidden` on player-backed resource routes.
`GET /api/v1/players/me` instead returns `404` when the authenticated identity
is not linked yet.

| Method | Path | Notes |
|---|---|---|
| `GET`, `POST` | `/api/v1/players/me` | Authenticated full-profile lookup and canonical onboarding. |
| `GET` | `/api/v1/players/me/occurrences` | The caller's own game schedule (occurrences where they hold a roster assignment). |
| `PUT`, `DELETE` | `/api/v1/players/{id}` | Self-only; caller must resolve to `{id}`. |
| `POST`, `PUT`, `DELETE` | `/api/v1/teams...` | Owner identity comes from `PlayerIdentity`; update/delete are owner-only. Leave is self-only; owners cannot remove another member through this route. |
| `GET`, `POST`, `PUT` | `/api/v1/joinrequests...` | Reads are limited to the applicant or relevant team owner; creation is by the linked applicant; processing is team-owner-only. |
| `POST`, `PUT`, `DELETE` | `/api/v1/events...` | Host identity comes from `PlayerIdentity`; updates, deletes and host transfer (`/{id}/host/transfer`) are host-only. When an event names a team, only that team's owner may create it. |
| `POST` | `/api/v1/events/{occurrenceId}/roster/...` | Requirement creation, host assignment, host removal and the day-of-game lifecycle (`check-in`, `activate`, `no-show`) are host-only. Any linked player may self-claim (`/claims`) and leave their own assignment (`/assignments/{id}/leave`). |
| `POST`, `DELETE` | `/api/v1/event-series...` | Host identity comes from `PlayerIdentity`; cancellation is host-only. When a series names a team, only that team's owner may create it. |
| `GET`, `POST`, `PUT`, `DELETE` | `/api/v1/rosterimports...` | Reads and mutations are restricted to the linked original importer. |
| `POST` | `/api/v1/venues` | Any linked player; the creator is recorded. |

### Anonymous endpoints (no token required)

| Method | Path |
|---|---|
| `GET` | `/healthz/live`, `/healthz/ready` (and the `/health`, `/health/ready` aliases) |
| `GET` | `/api/v1/teams`, `/api/v1/teams/{id}` |
| `GET` | `/api/v1/events`, `/api/v1/events/{id}`, `/api/v1/events/{id}/detail` |
| `GET` | `/api/v1/venues/{id}` (a Private venue is masked unless the caller created it) |
| `GET` | `/api/v1/discover/occurrences` (rate limited; a linked token adds the caller's relationship) |
| `GET` | `/api/v1/events/{occurrenceId}/roster`, `/api/v1/events/{occurrenceId}/roster/requirements`, `/api/v1/events/{occurrenceId}/roster/assignments` |
| `GET` | `/api/v1/event-series/{id}`, `/api/v1/event-series/{id}/occurrences` |
| `GET` | `/api/v1/players`, `/api/v1/players/{id}` |
| `GET` | `/api/v1/players/username/{username}` |
| `GET` | `/swagger` (Development only) |

Join-request reads are not anonymous: a single request is readable by its
applicant or the relevant team owner; team request lists are owner-only, and
player request lists are self-only. Roster-import lists and details are
importer-only because they may expose uploaded data such as `RawData`.

### Local development — issuing tokens with `dotnet user-jwts`

```powershell
cd src/TeamBuilder.Api

# The external subject may be any stable non-empty string, e.g. local-user-123.
dotnet user-jwts create --audience teambuilder-api --claim sub=local-user-123
```

Use the token to call `POST /api/v1/players/me` once to create and link a
player. Subsequent player-backed requests resolve that same exact issuer and
subject pair.

To align with TeamBuilder's `Jwt:SigningKey` config path, copy the generated
key into user-secrets:

```powershell
dotnet user-secrets set "Jwt:SigningKey" "<key-from-user-jwts>"
dotnet user-secrets set "Jwt:Issuer"     "dotnet-user-jwts"
```

See [`docs/auth-plan.md`](auth-plan.md) for all configuration keys.

## Error Responses

Validation errors use ASP.NET Core validation responses. The global exception
handler writes a JSON `ProblemDetails` response for unhandled exceptions;
controllers may also return route-specific error bodies.

### ProblemDetails envelope

| Field      | Type   | Description                                                      |
|------------|--------|------------------------------------------------------------------|
| `type`     | string | URI reference identifying the problem type (may be omitted).     |
| `title`    | string | Short, human-readable summary of the problem type.               |
| `status`   | int    | HTTP status code.                                                |
| `detail`   | string | Human-readable explanation specific to this occurrence.          |
| `traceId`  | string | May be added by ASP.NET Core error handling. |

### Status code mappings

| Scenario                                    | Status | Exception / source              |
|---------------------------------------------|--------|---------------------------------|
| Model validation failure (data annotations) | `400`  | `ValidationProblemDetails`      |
| Invalid argument (business rule)            | `400`  | `ArgumentException`             |
| Unauthenticated request on protected route  | `401`  | Auth middleware                 |
| Authenticated but not resource owner        | `403`  | Route-specific authorization check |
| Resource not found                          | `404`  | Controller/service result         |
| Conflict (duplicate or invalid state)       | `409`  | `InvalidOperationException`     |
| Unexpected server error                     | `500`  | Unhandled exception; generic client detail |

### ValidationProblemDetails (400 — model validation)

When ASP.NET Core model binding or data-annotation validation fails, the
response extends `ProblemDetails` with an `errors` dictionary keyed by field
name:

| Field    | Type                          | Description                         |
|----------|-------------------------------|-------------------------------------|
| `errors` | `object` (field → string[ ]) | One entry per invalid field.         |

### Example responses

#### 400 — Validation error

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Username": ["The Username field is required."],
    "Email": ["The Email field is not a valid e-mail address."]
  },
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

#### 404 — Not found

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "Player not found.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

#### 409 — Conflict

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "A pending join request already exists for this player and team.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

#### 500 — Unexpected error

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An unexpected error occurred.",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

---

## Endpoint Inventory

### Players — `api/v1/players`

#### `GET api/v1/players/me`

Returns the caller's full profile, including email, linked to their external
identity. Requires a valid JWT. The identity key is the token's exact `iss`
claim plus the claim named by `Jwt:ExternalIdentity:SubjectClaim` (default
`sub`; configure `oid` for Microsoft Entra where appropriate). Both values are
opaque and compared exactly; the subject does not need to be a GUID.

- **Response `200`:** `PlayerDto` of the linked player.
- **Response `401`:** No valid JWT, or the token has no usable issuer or subject.
- **Response `404`:** Authenticated, but no player is linked to this identity yet.

---

#### `GET api/v1/players/me/occurrences`

The caller's own game schedule: "what games am I committed to, when and where,
who hosts, am I confirmed, checked in or active, and is the roster ready?"
This is **occurrence participation, not team membership**: an occurrence is
listed when the caller holds a roster assignment on it, pickup games (`teamId`
null) and team events alike. By default hosting alone does not list an
occurrence; a host who plays claims a spot like anyone else. With
`includeHosted=true`, occurrences the caller hosts are listed too.

| Query | Default | Meaning |
|---|---|---|
| `fromUtc` | now | Occurrences not over yet at this instant: end (or start, when no end is recorded) at or after it. An `InProgress` occurrence always counts. |
| `toUtc` | none | Only occurrences starting before this instant. Earlier than `fromUtc` gives `400`. |
| `includeTerminal` | `false` | Also list occurrences where the caller's participation ended (left, removed, no-show). |
| `pageSize` | `20` | 1 to 100; other values fall back to 20. |
| `cursor` | none | The `nextCursor` of the previous page. An invalid cursor gives `400`. |
| `includeHosted` | `false` | Also list occurrences where the caller is the host (`EventOccurrence.HostId`) without a qualifying assignment. An occurrence the caller both hosts and plays in is listed once. |

Ordered nearest first by `scheduledStartUtc`, then occurrence id. Paging is
keyset-based, so it stays deterministic while occurrences are added; the last
page has `nextCursor` null. A page costs four queries whatever its size.

Each item: `occurrenceId`, `seriesId`, `teamId`, `name`, `scheduledStartUtc`,
`scheduledEndUtc`, `status`, `venueId`, `location` (venue name, else the
free-text location), `category`, `hostPlayerId`, `hostUsername`,
`hostDisplayName`, `isHost`, `myAssignmentId`, `myAssignmentStatus`,
`myRoleCode`, `myRequirementId`, and for the caller's requirement
`requiredCount`, `supplyCount` and `openQuantity` (null when the assignment is
linked to no requirement), plus the occurrence-level `isRosterReady`,
`totalRequiredCount`, `totalSupplyCount` and `totalOpenQuantity`. When the
caller has a live assignment it is the one shown; with `includeTerminal`, an
occurrence they only have history on shows their most recent row. An
organizer-only occurrence listed through `includeHosted` has `isHost: true`
and null `myAssignmentId`, `myAssignmentStatus`, `myRoleCode`,
`myRequirementId`, `requiredCount`, `supplyCount` and `openQuantity` (use the
`total*` fields for its roster). No email or external identity data is
returned.

- **Response `200`:** `{ "items": [...], "nextCursor": "..." | null }`.
- **Response `400`:** Invalid window or cursor.
- **Response `401`:** No valid JWT.
- **Response `403`:** Authenticated, but no player is linked to this identity.

---

#### `POST api/v1/players/me`

Onboards the caller: creates a new player with a server-generated `id` and
links the caller's external identity (issuer + subject) to it in one
transaction. This is the canonical player creation route and requires a JWT.
The request body uses `CreatePlayerDto`.

- **Response `201`:** Created `PlayerDto` with `Location: /api/v1/players/me`.
- **Response `400`:** Validation failure.
- **Response `401`:** No valid JWT, or the token has no usable issuer or subject.
- **Response `409`:** The identity is already linked to a player, or the
  username is taken. Concurrent onboarding of the same identity yields one
  `201` and `409` for the rest.

---

#### `GET api/v1/players/{id}`

Returns a public player profile by ID. This discovery endpoint is anonymous
and does not return email.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "username": "striker99",
  "displayName": "Striker",
  "bio": "Competitive FPS player",
  "region": "NA",
  "avatarUrl": "https://example.com/avatar.png",
  "createdAtUtc": "2025-01-01T00:00:00Z",
  "updatedAtUtc": null
}
```

**Response `404`:** Player not found.

---

#### `GET api/v1/players/username/{username}`

Returns a single player by username.

**Response `200`:** Same public profile shape as `GET /players/{id}`; email is omitted.
**Response `404`:** Player not found.

---

#### `GET api/v1/players`

Returns a paginated list of public player profiles. Email is omitted from each
item. Region filtering and pagination are supported.

**Query parameters:**

| Parameter | Type   | Description              |
|-----------|--------|--------------------------|
| `page`    | int    | Page number (default: 1) |
| `pageSize`| int    | Page size (default: 20)  |
| `region`  | string | Filter by region         |

**Response `200`:** `PaginatedResult<PublicPlayerDto>`

---

#### `POST api/v1/players` (removed)

The former anonymous player-creation route has been removed. A `POST` to the
collection path is rejected with `405 Method Not Allowed` because the path
continues to serve `GET` discovery. Use authenticated
`POST /api/v1/players/me` for onboarding.

---

#### `PUT api/v1/players/{id}`

Updates the caller's own existing player profile. Requires authentication and
the resolved caller `Player.Id` must match `{id}`; only non-null fields are
applied. Email may be updated through this endpoint but remains excluded from
public player discovery responses.

**Request body:**

```json
{
  "email": "new@example.com",
  "displayName": "New Name",
  "bio": "Updated bio",
  "region": "EU",
  "avatarUrl": "https://example.com/new-avatar.png"
}
```

**Response `200`:** Updated `PlayerDto`.  
**Response `404`:** Player not found.  
**Response `400`:** Validation failure.
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller is not this player.

---

#### `DELETE api/v1/players/{id}`

Deletes the caller's player profile. Requires authentication and succeeds only
when the resolved caller `Player.Id` matches `{id}`.

On success the player's team memberships (active and inactive) are deleted in
the same transaction, and every team where the player was an active member has
`currentMemberCount` reconciled. The freed slot shows up in the team's derived
`openSlots`/`isFull`; no team status or recruitment setting is changed.

**Response `204`:** Deleted.  
**Response `404`:** Player not found.
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller is not this player.
**Response `409`:** The player owns a team (delete or transfer it first), or a
team ownership, membership or roster count changed concurrently; nothing was
deleted, so retry.

---

### Teams — `api/v1/teams`

#### `GET api/v1/teams/{id}`

Returns a single team by ID. Includes the owner's username.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000002",
  "name": "Alpha Squad",
  "description": "Competitive FPS team",
  "lifecycleStatus": "Active",
  "isAcceptingMembers": true,
  "currentMemberCount": 3,
  "maxMembers": 10,
  "openSlots": 7,
  "isFull": false,
  "hasVacancies": true,
  "status": "Recruiting",
  "region": "NA",
  "category": "FPS",
  "tags": "fps,competitive",
  "ownerId": "00000000-0000-0000-0000-000000000001",
  "ownerUsername": "striker99",
  "createdAtUtc": "2025-01-01T00:00:00Z",
  "updatedAtUtc": null
}
```

**Response `404`:** Team not found.

**Team state fields.** A team's administrative lifecycle, its recruitment
policy and its physical capacity are separate facts:

| Field | Stored? | Meaning |
|---|---|---|
| `lifecycleStatus` | yes | `TeamLifecycleStatus`: `Active`, `Inactive`, `Disbanded`. |
| `isAcceptingMembers` | yes | Whether the owner accepts new join requests. Always `false` unless `lifecycleStatus` is `Active`. |
| `currentMemberCount` / `maxMembers` | yes | Active roster size and capacity. |
| `openSlots` | derived | `max(0, maxMembers - currentMemberCount)`. |
| `isFull` | derived | `currentMemberCount >= maxMembers`. |
| `hasVacancies` | derived | `lifecycleStatus == Active && isAcceptingMembers && openSlots > 0`. |
| `status` | derived | **Deprecated** legacy `TeamStatus`, kept for compatibility (see below). |

Joins, leaves, player deletion and `maxMembers` changes only change the member
count / capacity, and therefore the derived fields; they never change
`lifecycleStatus` or `isAcceptingMembers`. Reaching capacity does not close
recruitment, and gaining a vacancy does not reopen it.

**Legacy `status` (deprecated).** `status` is never stored. It is computed as:

| Condition | `status` |
|---|---|
| `lifecycleStatus == Inactive` | `Inactive` |
| `lifecycleStatus == Disbanded` | `Disbanded` |
| `Active` and `isFull` | `Full` |
| `Active`, not full, `isAcceptingMembers` | `Recruiting` |
| `Active`, not full, not accepting | `Active` |

New clients should read `lifecycleStatus`, `isAcceptingMembers`, `isFull` and
`hasVacancies` instead.

---

#### `GET api/v1/teams`

Returns a paginated list of teams.

**Query parameters:**

| Parameter  | Type       | Description              |
|------------|------------|--------------------------|
| `page`     | int        | Page number (default: 1) |
| `pageSize` | int        | Page size (default: 20)  |
| `category` | string     | Filter by category       |
| `region`   | string     | Filter by region         |
| `lifecycleStatus` | TeamLifecycleStatus | Filter by lifecycle (`Active`, `Inactive`, `Disbanded`) |
| `hasVacancies` | bool | `true`: `Active` and accepting and `currentMemberCount < maxMembers`. `false`: every other team (the exact inverse). |
| `status`   | TeamStatus | **Deprecated** legacy filter, translated as below |

Legacy `status` filter translation (matches the computed `status` field):

| `status` | Matches |
|---|---|
| `Recruiting` | `Active` and accepting and `currentMemberCount < maxMembers` |
| `Full` | `Active` and `currentMemberCount >= maxMembers` |
| `Active` | `Active` and not accepting and `currentMemberCount < maxMembers` |
| `Inactive` | `lifecycleStatus == Inactive` |
| `Disbanded` | `lifecycleStatus == Disbanded` |

All supplied filters (including legacy `status` together with `lifecycleStatus`
/ `hasVacancies`) combine with AND semantics. `totalCount` reflects the filters
before paging.

**Response `200`:** `PaginatedResult<TeamDto>`

---

#### `POST api/v1/teams`

Creates a new team. Requires a linked caller identity; `OwnerId` is set to the
resolved internal `Player.Id`, not copied from a token claim. A new team is
`lifecycleStatus: Active`, `isAcceptingMembers: true` with `currentMemberCount: 0`
(legacy `status`: `Recruiting`). The owner is not added as a `TeamMember`.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Request body:**

```json
{
  "name": "Alpha Squad",
  "description": "Competitive FPS team",
  "maxMembers": 10,
  "region": "NA",
  "category": "FPS",
  "tags": "fps,competitive"
}
```

**Response `201`:** Created `TeamDto`.  
**Response `400`:** Validation failure.

---

#### `PUT api/v1/teams/{id}`

Updates an existing team. Only non-null fields are applied.
An explicit empty string for `description` clears the value.

**Request body:**

```json
{
  "name": "Alpha Squad Revised",
  "description": "Updated description",
  "lifecycleStatus": "Active",
  "isAcceptingMembers": false,
  "maxMembers": 12,
  "region": "EU",
  "category": "FPS",
  "tags": "fps,competitive,ranked"
}
```

Lifecycle and recruitment rules:

- `lifecycleStatus` / `isAcceptingMembers` set the stored state; omitted values
  are kept.
- Moving to `Inactive` or `Disbanded` closes recruitment automatically.
  Requesting `isAcceptingMembers: true` while the resulting lifecycle is
  `Inactive`/`Disbanded` is a `400`.
- Reactivating an `Inactive`/`Disbanded` team does not reopen recruitment: unless
  `isAcceptingMembers: true` is sent in the same request, it stays `false`.
- **Deprecated** legacy `status` input is still accepted on its own:
  `Recruiting` → `Active` + accepting; `Active` → `Active` + not accepting;
  `Inactive` / `Disbanded` → that lifecycle + not accepting. `Full` is rejected
  with `400` ("Full is derived from roster capacity and cannot be set directly.").
  Sending `status` together with `lifecycleStatus` or `isAcceptingMembers` is a
  `400`.

**Response `200`:** Updated `TeamDto`.  
**Response `404`:** Team not found.  
**Response `400`:** Validation failure, or an invalid lifecycle/recruitment combination (see above).
**Response `409`:** `maxMembers` below the active member count, or a concurrent change.
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller is not the team owner.

---

#### `DELETE api/v1/teams/{id}`

Deletes a team.

**Response `204`:** Deleted.  
**Response `404`:** Team not found.
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller is not the team owner.

---

#### `POST api/v1/teams/{teamId}/members/{playerId}/leave`

Allows a player to leave their own team membership only: the authenticated
player ID must match `{playerId}`. A team owner cannot remove another player
through this voluntary-leave route. Marks the `TeamMember` record as inactive.
Reconciles `CurrentMemberCount`; the freed slot shows up in the derived
`openSlots`/`isFull`. Leaving never changes `lifecycleStatus` or
`isAcceptingMembers`.

**Response `204`:** Member removed.  
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller does not match `{playerId}`.
**Response `404`:** Active team member not found.

---

### Join Requests — `api/v1/joinrequests`

#### `GET api/v1/joinrequests/{id}`

Returns a single join request by ID. Includes team and player usernames.
Requires authentication; only the applicant or the relevant team owner may
read it.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000003",
  "teamId": "00000000-0000-0000-0000-000000000002",
  "teamName": "Alpha Squad",
  "playerId": "00000000-0000-0000-0000-000000000001",
  "playerUsername": "striker99",
  "status": "Pending",
  "message": "I'd love to join!",
  "requestedAtUtc": "2025-01-02T00:00:00Z",
  "processedAtUtc": null
}
```

**Response `404`:** Join request not found.

---

#### `GET api/v1/joinrequests/teams/{teamId}`

Returns paginated join requests for a team. Requires authentication and is
restricted to that team's owner.

**Query parameters:**

| Parameter  | Type          | Description              |
|------------|---------------|--------------------------|
| `page`     | int           | Page number (default: 1) |
| `pageSize` | int           | Page size (default: 20)  |
| `status`   | RequestStatus | Filter by status         |

`RequestStatus` values: `Pending`, `Approved`, `Rejected`, `Cancelled`

**Response `200`:** `PaginatedResult<JoinRequestDto>`

---

#### `GET api/v1/joinrequests/players/{playerId}`

Returns paginated join requests for a player. Requires authentication; callers
may read only their own requests.

**Query parameters:** Same as `GET /joinrequests/teams/{teamId}`.

**Response `200`:** `PaginatedResult<JoinRequestDto>`

---

#### `POST api/v1/joinrequests`

Submits a join request. Only one pending request per player per team is allowed.
Requires a linked caller identity; `PlayerId` is set to the resolved internal
`Player.Id`, not copied from a token claim.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Request body:**

```json
{
  "teamId": "00000000-0000-0000-0000-000000000002",
  "message": "I'd love to join!"
}
```

The team must be `lifecycleStatus: Active` with `isAcceptingMembers: true`.
Physical capacity is deliberately not checked: a full team that is still
accepting members may collect pending requests (approval still enforces
capacity).

**Response `201`:** Created `JoinRequestDto`.  
**Response `400`:** Validation failure.
**Response `404`:** Team not found.
**Response `409`:** The team is inactive/disbanded ("Team is not active."), the team
is not accepting members ("Team is not currently accepting new members."), or a
pending request already exists for this player and team.

---

#### `PUT api/v1/joinrequests/{id}/process`

Processes (approves, rejects, or cancels) a pending join request. Only the
team owner may process the request, and only `Pending` requests can be
processed. Approving a request:

- Requires the team to be `lifecycleStatus: Active` (it does **not** require
  `isAcceptingMembers`, so closing recruitment never blocks existing requests).
- Requires a free slot (active members < `maxMembers`).
- Creates a new `TeamMember` record and reconciles `Team.CurrentMemberCount`;
  reaching capacity shows up only in the derived `isFull`.

Requires `Authorization: Bearer <token>` for the processing user.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Request body:**

```json
{
  "status": "Approved"
}
```

**Response `200`:** Updated `JoinRequestDto`.  
**Response `401`:** No valid JWT provided.
**Response `403`:** Authenticated caller is not the team owner.
**Response `404`:** Join request not found.  
**Response `409`:** Request is not pending, the team is not active (approval only), or the team is already full.

---

### Events — `api/v1/events`

Internally, every event served by these routes is an **event occurrence**: one
concrete game or session (`EventOccurrence`, still stored in the `Events`
table). The API is unchanged for existing clients:

- `eventDateUtc` is still accepted on create/update and still returned; it is
  the occurrence's scheduled start and always equals `scheduledStartUtc`.
- `location` is still accepted and returned as free display text. It is stored
  as legacy location text and is never geocoded or turned into a venue. When an
  occurrence has a venue, `location` returns the venue's name.
- Responses also carry additive fields: `scheduledStartUtc`, `scheduledEndUtc`
  (null when no end is known, which includes every pre-existing event),
  `seriesId`, `venueId` and `isDetached`.
- An event created through `POST` is a one-off occurrence: it has no series,
  and a pickup/community event has no team.

Occurrences generated from a recurring series (see
[Event Series](#event-series--apiv1event-series)) are served by these routes
too, with `seriesId` set. An occurrence can be held at a venue from
[Venues](#venues--apiv1venues); games at a physical venue with coordinates are
found by [Discovery](#discovery--apiv1discover).

All scheduled instants (`eventDateUtc`, `scheduledStartUtc`,
`scheduledEndUtc`) are UTC and serialized with a `Z` suffix.

#### `GET api/v1/events/{id}`

Returns a single event by ID. Includes team name and host username.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000004",
  "name": "Spring Championship",
  "description": "Annual spring tournament",
  "eventDateUtc": "2025-04-01T18:00:00Z",
  "scheduledStartUtc": "2025-04-01T18:00:00Z",
  "scheduledEndUtc": null,
  "status": "Planned",
  "category": "FPS",
  "tags": "fps,tournament",
  "location": "Online",
  "seriesId": null,
  "venueId": null,
  "isDetached": false,
  "region": "NA",
  "maxParticipants": 64,
  "currentParticipantCount": 0,
  "teamId": "00000000-0000-0000-0000-000000000002",
  "teamName": "Alpha Squad",
  "hostId": "00000000-0000-0000-0000-000000000001",
  "hostUsername": "striker99",
  "createdAtUtc": "2025-01-01T00:00:00Z",
  "updatedAtUtc": null
}
```

**Response `404`:** Event not found.

---

#### `GET api/v1/events/{id}/detail`

Public game-page view of one occurrence, in three queries: details,
`location` (venue name, else free-text), host (`hostPlayerId`,
`hostUsername`, `hostDisplayName`), `status` and `acceptsRosterChanges`
(false once Completed, Cancelled or Archived), the requirements with
`requiredCount`/`supplyCount`/`openQuantity`, the occurrence totals and
`isRosterReady`, and the **current participants only** (supply statuses, in
join order: `assignmentId`, `playerId`, `username`, `displayName`,
`requirementId`, `roleCode`, `status`, `isHost`). Ended rows are not listed;
history stays on the paged `…/roster/assignments` route. The caller's
relationship (`isHost`, `myAssignmentId`, `myAssignmentStatus`,
`myRequirementId`) is filled from a valid linked token and is false/null for
anonymous or unlinked callers. `mySubscribedRequirementIds` lists the
requirements the caller has asked to be notified about (see
[vacancy notifications](#vacancy-notifications)); it is empty for anonymous
callers and current participants, and never reveals anyone else's
subscriptions. No email or external identity data is returned.

`venue` (null without a venue) is the attached venue after privacy masking:
for a **Private** venue the street address, postal code and coordinates are
returned only to the host and to callers with a current (supply-status)
assignment; everyone else gets `isAddressMasked: true` with the name, city,
state/province and country only. A participant who leaves loses the address.

**Response `200`:** `OccurrenceDetailDto`.
**Response `404`:** Event not found.

---

#### `GET api/v1/events`

Returns a paginated list of events, ordered by scheduled start (`eventDateUtc`) ascending.

**Query parameters:**

| Parameter  | Type        | Description              |
|------------|-------------|--------------------------|
| `page`     | int         | Page number (default: 1) |
| `pageSize` | int         | Page size (default: 20)  |
| `category` | string      | Filter by category       |
| `region`   | string      | Filter by region         |
| `status`   | EventStatus | Filter by status         |

`EventStatus` values: `Planned`, `Open`, `InProgress`, `Completed`, `Cancelled`

**Response `200`:** `PaginatedResult<EventDto>`

---

#### `POST api/v1/events`

Creates a new event. Requires a linked caller identity; `HostId` is set to the
resolved internal `Player.Id`. If `teamId` is supplied, the caller must own
that team, and the team's `lifecycleStatus` must be `Active`. Recruitment
(`isAcceptingMembers`) and capacity (`isFull`) never affect event creation.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Request body:**

```json
{
  "name": "Spring Championship",
  "description": "Annual spring tournament",
  "eventDateUtc": "2025-04-01T18:00:00Z",
  "category": "FPS",
  "tags": "fps,tournament",
  "location": "Online",
  "region": "NA",
  "maxParticipants": 64,
  "teamId": "00000000-0000-0000-0000-000000000002"
}
```

**Optional initial roster (atomic).** The same request may also create the
occurrence's roster requirements and the host's own spot. Everything is saved
in one database transaction: if any part fails, nothing is created.

```json
{
  "name": "Wednesday 8 PM pickup basketball",
  "eventDateUtc": "2026-10-15T03:00:00Z",
  "scheduledEndUtc": "2026-10-15T05:00:00Z",
  "category": "basketball",
  "location": "Rec center court 2",
  "rosterRequirements": [ { "roleCode": "participant", "requiredCount": 10 } ],
  "hostParticipates": true
}
```

| Field | Default | Meaning |
|---|---|---|
| `scheduledEndUtc` | none | Optional scheduled end; must be after `eventDateUtc`. |
| `rosterRequirements` | none | 0 to 50 requirements, each validated like `POST …/roster/requirements` (role code normalization, `requiredCount` 1–100000). Role codes must be distinct after normalization. Omitted or empty keeps the existing behavior: an event with no roster, never roster-ready. |
| `hostParticipates` | `false` | `false`: the host organizes only and holds no spot (0/N). `true`: the host gets a normal Confirmed assignment (source `Player`) through the same capacity rules as a self-claim (1/N). |
| `hostRoleCode` | none | With `hostParticipates` and several requirements, the role code the host fills. Optional with exactly one requirement. |
| `venueId` | none | A venue from `POST /api/v1/venues`. Any Public (or Virtual) venue may be used; a **Private** venue only by the player who created it (hosting it would reveal its address). Only games at a physical venue with coordinates appear in discovery. `location` stays optional free text. |

Nothing is activity-specific: the API never assumes 10 players for
basketball; the client sends the count.

**Response `201`:** Created `EventDto`.  
**Response `400`:** Validation failure. Roster errors carry a stable `code`:
`HostParticipationRequiresRequirement` (`hostParticipates` with no
requirement), `DuplicateRequirementRoleInRequest`, `HostRoleCodeInvalid`
(missing with several requirements, or not one of them).
**Response `401`:** No valid JWT provided.
**Response `403`:** Caller has no linked player, does not own the supplied team, or used someone else's Private venue.
**Response `404`:** Supplied team or venue not found.
**Response `409`:** Supplied team is inactive or disbanded.

---

#### `PUT api/v1/events/{id}`

Updates an existing event. Only non-null fields are applied.

When the event is an occurrence generated by a recurring series (`seriesId`
is set), any successful `PUT` marks it detached (`isDetached: true`), even if
no value actually changes: it has been customized independently of the series.
It keeps its `seriesId` (and its stored position in the series), is never
regenerated at its original slot, and is skipped by series cancellation.
One-off events (`seriesId: null`) are never detached.

**Request body:**

```json
{
  "name": "Spring Championship 2025",
  "status": "Open",
  "maxParticipants": 128
}
```

**Response `200`:** Updated `EventDto`.  
**Response `400`:** Validation failure.  
**Response `401`:** No valid JWT provided.  
**Response `403`:** Authenticated caller is not the event host.  
**Response `404`:** Event not found.  
**Response `409`:** Event has no host (orphaned); contact an administrator.

---

#### `DELETE api/v1/events/{id}`

Deletes an event that has **no roster participation history**. An event with
any roster assignment, live or historical (left, removed, no-show), cannot be
deleted, so participation history never disappears: cancel it instead
(`PUT` with `status` `Cancelled`). Its roster requirements and imported roster
entries are deleted with it. The database enforces the same rule: the foreign
key from `RosterAssignments` to `Events` is `NO ACTION`, so a claim that lands
while the delete is running also turns it into `409`.

**Response `204`:** Deleted.  
**Response `401`:** No valid JWT provided.  
**Response `403`:** Authenticated caller is not the event host.  
**Response `404`:** Event not found.  
**Response `409`:** Event has no host (orphaned); contact an administrator.
Or `code` `OccurrenceHasParticipationHistory`: the event has roster
assignments; cancel it instead. Or `code` `OccurrenceChanged`: the event
changed concurrently; reload and retry.

---

#### `POST api/v1/events/{id}/host/transfer`

Transfers stewardship of **this occurrence only** to another player. For an
occurrence of a series, the series and its other occurrences keep their host
(a separate series-level transfer may come later); the occurrence keeps its
`seriesId` and is marked `isDetached`, like any other occurrence-level edit,
so a later series cancellation leaves it to its new host.

```json
{ "newHostPlayerId": "<guid>" }
```

Only the host changes. Roster assignments, requirements, capacity, `teamId`
and `status` are untouched, and the new host may or may not hold a roster
spot. The old host loses host-only authority (event edits, roster management,
lifecycle actions, transfer) the moment the transfer commits, and the new host
gains it. Host-only roster actions re-check the host inside their own commit,
so one that was in flight when the transfer committed is refused with `403`.
Transfers are guarded by the event's `RowVersion`: of concurrent transfers
exactly one wins, and the others get `409` `OccurrenceChanged` (on reload they
are no longer the host). Transferring to yourself is a no-op `200`.

Checked in this order: no valid JWT `401`, unlinked caller `403`, missing
occurrence `404`, occurrence without a host `409` `OccurrenceHasNoHost`, caller
not the host `403`, missing or empty `newHostPlayerId` `400`, unknown player
`400`, player with no linked sign-in identity `409`
`HostTransferTargetNotLinked`.

Only a live occurrence changes host: `Planned`, `Open` and `InProgress` can
be transferred; `Completed`, `Cancelled` and `Archived` give `409`
`OccurrenceClosed` (checked right after the host check), so the stewardship
of a finished occurrence is never rewritten. A status change racing the
transfer makes it `409` `OccurrenceChanged`. Leaving a game never transfers
hosting automatically.

**Response `200`:** The updated `EventDto`.

---

### Event Roster — `api/v1/events/{occurrenceId}/roster`

Live participation for one event occurrence. **Event participation is
independent of team membership**: any existing player can be assigned to an
occurrence, whether or not they belong to its team (pickup games, substitutes,
free agents).

- **RosterRequirement** is quantity-based demand: "this occurrence needs
  `requiredCount` players in role `roleCode`". There are no numbered slots.
  `roleCode` follows the TBRL code grammar (`^[a-z0-9][a-z0-9._-]*$`, at most
  100 characters), is trimmed and lowercased, and is interpreted within the
  activity (`guard`, `tank`, `healer`, …). Generic demand uses the canonical
  code `participant`, which is also the default when `roleCode` is omitted.
  An occurrence has at most one requirement per role code.
- **RosterAssignment** is a player's participation state for the occurrence.
  Rows are history: a departure or no-show stays on its row, and a replacement
  is a new row whose `replacedAssignmentId` points back to it.
- **RosterEntry** (roster imports) is imported/raw roster provenance and is
  not live participation.

Assignment `status` values (integers): `1` Reserved, `2` Confirmed,
`3` CheckedIn, `4` Active, `5` Departed, `6` NoShow, `7` Cancelled.
Reserved, Confirmed, CheckedIn and Active hold roster **supply**; the others
are historical. A player holds at most one supply assignment per occurrence
(enforced by a filtered unique index). `source`: `1` Player, `2` Host,
`3` Import, `4` System. `exitReason`: `1` PlayerLeft, `2` HostRemoved,
`3` NoShow, `4` Replaced, `5` Other.

For each requirement, `supplyCount` counts its linked supply assignments and
`openQuantity = max(0, requiredCount - supplyCount)`. Open quantity is derived
on read and never stored. Assignments without a `requirementId` count toward
no requirement. The legacy `EventDto.currentParticipantCount` and
`maxParticipants` are neither changed nor consulted by roster operations.

**Readiness.** `isRosterReady` is true only when the occurrence has at least
one requirement and every requirement has `openQuantity` 0. An occurrence with
no requirements is never ready. The occurrence-level `openQuantity` is the sum
of per-requirement open quantities, so surplus on one role never fills
another. Example (5-v-5 pickup basketball, one `participant` requirement of
10): 0 assigned is 0/10, 9 is 9/10 not ready, 10 is 10/10 ready.

**No overbooking.** Host assignment and player self-claim share one
allocation path. An assignment linked to a requirement is rejected with `409`
once that requirement's supply equals its `requiredCount`. The check runs in
the same transaction as the insert, guarded by the requirement's `RowVersion`
(each linked assignment touches the requirement row, advancing its
`updatedAtUtc`), so concurrent fills of the last spot cannot both commit.
A request that loses such a race gets `409` with code `RequirementFull` when
the competing commit took the last open quantity, or `RosterChanged` when
open quantity remains; `RosterChanged` is safe to retry. The server does not
retry on its own.

**Who holds a spot.** Only assignments hold roster supply. The occurrence's
host is an administrator and holds no spot unless they claim one through the
same self-claim endpoint as everyone else. Standalone pickup occurrences
(`teamId` null) and team occurrences use the same roster; claiming never
requires team membership.

**Conflict codes.** Roster `409` responses are ProblemDetails with a stable
`code`: `AlreadyParticipating`, `RequirementFull`, `RosterChanged`,
`OccurrenceClosed`, `AssignmentEnded`, `AssignmentTransitionInvalid`,
`OccurrenceHasNoHost`, `ReplacedAssignmentStillActive` or
`DuplicateRequirementRole`. Roster `400` responses for host assignment carry
`RequirementIdRequired` or `RequirementNotOnOccurrence`. Database messages are
never exposed.

**Joinable statuses.** `Planned`, `Open` and `InProgress` occurrences accept
roster changes, including self-claims, so a player who leaves mid-game can be
replaced while the game is underway. `Completed`, `Cancelled` and `Archived`
occurrences are closed.

Host mutations (requirements, host assignment, host removal, lifecycle
actions) are checked in this order: unlinked identity `403`, missing
occurrence `404`, occurrence without a host `409`, caller not the host `403`,
occurrence `Completed`/`Cancelled`/`Archived` `409`. For every host mutation
(requirement creation, host assignment, host removal and the lifecycle
actions) these checks run inside the service and are re-validated in the same
commit as the change (the occurrence row is updated with a `RowVersion`
check), so a concurrent host transfer or status change is never ignored: a
former host gets `403`, a closed occurrence `409` `OccurrenceClosed`, an
orphaned one `409` `OccurrenceHasNoHost`. The occurrence's `updatedAtUtc`
advances on each such action. A change that only lost to another change on
the same occurrence or requirement is re-run from fresh state, at most five
times, before `409` `RosterChanged` (retryable); there is no unbounded retry.

#### `GET api/v1/events/{occurrenceId}/roster`

Public readiness summary:

```json
{
  "occurrenceId": "…",
  "requiredCount": 10,
  "supplyCount": 9,
  "openQuantity": 1,
  "isRosterReady": false,
  "requirements": [ { "roleCode": "participant", "requiredCount": 10, "supplyCount": 9, "openQuantity": 1, "…": "…" } ],
  "assignments": [ { "playerId": "…", "username": "…", "status": 2, "…": "…" } ]
}
```

`supplyCount` counts every supply assignment, including ones linked to no
requirement. `assignments` lists every assignment, history included, in
creation order, with the same shape as the assignments endpoint. **Response
`404`:** occurrence not found.

#### `GET api/v1/events/{occurrenceId}/roster/requirements`

Public. Returns the occurrence's requirements (array, creation order) with
`id`, `occurrenceId`, `roleCode`, `displayPosition`, `sourceRoleLabel`,
`requiredCount`, `supplyCount`, `openQuantity`, `createdAtUtc`,
`updatedAtUtc`. **Response `404`:** occurrence not found.

#### `POST api/v1/events/{occurrenceId}/roster/requirements`

```json
{ "roleCode": "guard", "displayPosition": "Guard", "sourceRoleLabel": "Point Guard", "requiredCount": 2 }
```

`requiredCount` is required, 1–100000. **Response `201`:** the requirement.
**`400`:** validation failure. **`403`:** not the host (including a host who
was replaced while the request was in flight). **`409`:** `code`
`DuplicateRequirementRole`, `OccurrenceHasNoHost`, `OccurrenceClosed` or
`RosterChanged`.

#### `GET api/v1/events/{occurrenceId}/roster/assignments`

Public, paginated (`page`, `pageSize` ≤ 100, default 50), all statuses in
creation order. Each item: `id`, `occurrenceId`, `playerId`, `username`,
`displayName`, `requirementId`, `roleCode`, `sourceRoleLabel`, `status`, `source`,
`reservedAtUtc`, `confirmedAtUtc`, `checkedInAtUtc`, `activatedAtUtc`,
`departedAtUtc`, `exitReason`, `replacedAssignmentId`, `createdAtUtc`,
`updatedAtUtc`. No email or external identity data is exposed.
**Response `404`:** occurrence not found.

#### `POST api/v1/events/{occurrenceId}/roster/assignments`

The host assigns any existing player (source `Host`), through the same
allocation and capacity guard as player self-claim.

```json
{ "playerId": "…", "requirementId": "…", "status": 2, "replacedAssignmentId": null }
```

- `status` defaults to Confirmed and must be a supply status; the matching
  timestamp (`reservedAtUtc`, `confirmedAtUtc`, …) is set to now.
- `requirementId` is **required**: every public assignment fills a
  requirement of this occurrence, so it always counts toward capacity and
  readiness. Omitted: `400` `RequirementIdRequired` (checked with the body,
  before the host checks). A requirement of another occurrence, or an unknown
  one: `400` `RequirementNotOnOccurrence` (this endpoint has always answered
  `400` for that; player self-claim answers `404`). `roleCode` defaults to the
  requirement's code and must match it when both are given.
- `replacedAssignmentId` must name an assignment of this occurrence that no
  longer holds supply; that row is not modified.

**Response `201`:** the assignment. **`400`:** validation failure, missing
requirement, unknown player, requirement or replaced assignment of another
occurrence, conflicting role code. **`403`:** not the host (re-checked at
commit). **`409`:** `AlreadyParticipating`, `RequirementFull`,
`RosterChanged` (after bounded re-validation), `ReplacedAssignmentStillActive`,
`OccurrenceHasNoHost` or `OccurrenceClosed`.

#### `POST api/v1/events/{occurrenceId}/roster/claims`

The authenticated caller claims one unit of open quantity on a requirement.
The claimant is always the caller (resolved from `PlayerIdentity`); any
linked player may claim, the host included, and no team membership is
required.

```json
{ "requirementId": "…", "replacesAssignmentId": null }
```

- The new assignment is `Confirmed` with source `Player` and
  `confirmedAtUtc` set. There is no reservation or expiry step.
- `replacesAssignmentId` is optional replacement lineage. It must name an
  assignment of this occurrence, on the same requirement, that no longer holds
  supply; that row is not modified. Generic refill does not need it.
- A retry is safe: when the caller already holds a live assignment on the same
  requirement (for example after a lost response), it is returned with `200`
  and nothing new is created. This also holds when the requirement has filled
  since.

Checked in this order: no token `401`, unlinked identity `403`, missing
occurrence `404`, requirement not found on this occurrence (including a
requirement of another occurrence) `404`, closed occurrence `409`
`OccurrenceClosed`, then the existing-claim check, then the replacement rules
(`400` unknown, other occurrence or other requirement; `409`
`ReplacedAssignmentStillActive`), then capacity.

**Response `201`:** the new assignment (`Location` is the assignments list).
**`200`:** the caller's existing assignment on this requirement. **`400`:**
validation failure (missing or empty `requirementId`). **`409`:**
`AlreadyParticipating` (the caller holds a live assignment on another
requirement of this occurrence), `RequirementFull`, `RosterChanged`
(retryable), `OccurrenceClosed`, `ReplacedAssignmentStillActive`.

#### `POST api/v1/events/{occurrenceId}/roster/assignments/{assignmentId}/leave`

The caller ends their own live assignment. Nothing is deleted:

- `Reserved` or `Confirmed` (never played) becomes `Cancelled`;
- `CheckedIn` or `Active` (played) becomes `Departed`;
- `exitReason` becomes `PlayerLeft` and `departedAtUtc` records the exit time
  in both cases.

The spot reopens in the same commit, so the summary immediately shows the
lower supply and `isRosterReady` false (10/10 ready becomes 9/10 with
`openQuantity` 1). Anyone can then claim it.

Checked in this order: no token `401`, unlinked identity `403`, missing
occurrence or assignment (including an assignment of another occurrence)
`404`, assignment held by someone else `403` (the host included; the host
uses remove), closed occurrence `409` `OccurrenceClosed`, assignment already
`Departed`/`NoShow`/`Cancelled` `409` `AssignmentEnded`. A concurrent
transition of the same assignment returns `409` (`AssignmentEnded` when it
has ended). **Response `200`:** the ended assignment.

#### `POST api/v1/events/{occurrenceId}/roster/assignments/{assignmentId}/remove`

The host ends a participant's live assignment, with the same transitions and
history retention as leave and `exitReason` `HostRemoved`. The host may also
remove their own claim. Host checks first (see above), then missing
assignment `404` and already ended `409` `AssignmentEnded`. **Response
`200`:** the ended assignment.

**Rapid refill example** (Wednesday 8 PM 5-on-5 pickup, one `participant`
requirement of 10, no team): the host creates the occurrence and the
requirement (0/10), optionally claims (1/10), players claim until 9/10, two
players race for the last spot (one `201`, one `409` `RequirementFull`, 10/10
ready), a player leaves (9/10, not ready, their row kept as `Cancelled`), and
a new player claims (10/10 ready again). The same works while the occurrence
is `InProgress`.

A player with any roster assignment cannot be deleted
(`DELETE api/v1/players/{id}` returns `409`), and neither can an occurrence
(`DELETE api/v1/events/{id}` returns `409` `OccurrenceHasParticipationHistory`),
so participation history is never erased.

#### Day-of-game lifecycle (host only)

Narrow actions on one existing assignment, never arbitrary status writes:

| Route (`POST …/roster/assignments/{assignmentId}/…`) | From | To | Holds a spot after |
|---|---|---|---|
| `check-in` | Confirmed | CheckedIn (`checkedInAtUtc`) | yes |
| `activate` | CheckedIn | Active (`activatedAtUtc`) | yes |
| `no-show` | Confirmed or CheckedIn | NoShow (`exitReason` NoShow, `departedAtUtc`) | no: the spot reopens at once |

The row is updated in place and never deleted; a no-show's replacement is a
new row (optionally with `replacesAssignmentId`). Ended rows (Departed,
NoShow, Cancelled) are never resurrected: `409` `AssignmentEnded`. Any other
step not in the table (for example activating a player who has not checked
in, or a Reserved row) is `409` `AssignmentTransitionInvalid`. Leave and
remove keep their behavior: Confirmed rows end as Cancelled, CheckedIn and
Active rows as Departed.

Order: no valid JWT `401`, unlinked `403`, missing occurrence `404`, no host
`409` `OccurrenceHasNoHost`, caller not the host `403` (the participant
themselves included), closed occurrence `409` `OccurrenceClosed`, assignment
missing or of another occurrence `404`, then the state rules above.
**Response `200`:** the updated assignment.

The host may run these on their own assignment if they claimed one. Typical
flow: players claim, the host checks them in as they arrive, sets the event
`InProgress` and activates them at tip-off; a no-show or an early leaver
reopens a spot that anyone can claim while the game is in progress.

---

### Vacancy notifications

**Current (TB-REFILL-001):** a player on a full game can ask to be told, in
the app, when a spot opens in a specific requirement. It is a hint, not a
reservation: the spot stays first-come, first-served through the normal
claim endpoint, and nobody is ever joined automatically.

**How a vacancy is detected.** Leave, host remove and no-show write a
`roster.vacancy.opened.v1` message to the `OutboxMessages` table **in the
same database commit** as the roster change (and, for host actions, the
occurrence `RowVersion` update). If the commit fails, neither exists. A
message is written only when the requirement's open quantity actually rises
(`openAfter > openBefore`); a departure from an overfilled requirement opens
nothing. Check-in, activate, claims, host assignment, already-ended
assignments and closed occurrences never write one. The deduplication key is
`roster.vacancy.opened.v1:{vacatedAssignmentId}`, unique in the database, so
one departure yields at most one message.

The message payload holds ids and counts only (no names, emails or
coordinates): `eventId`, `occurrenceId`, `rosterRequirementId`, `roleCode`,
`vacatedAssignmentId`, `reason` (`PlayerLeft`, `HostRemoved` or `NoShow`),
`previousOpenQuantity`, `openQuantity`, `requiredCount`,
`occurrenceStartUtc`, `venueId` and `occurredAtUtc`.

**Delivery.** A background worker in the API process (`Outbox` settings:
`Enabled`, `PollInterval`, `BatchSize`, `LeaseDuration`, `MaxAttempts`,
`RetryBaseDelay`, `RetryMaxDelay`) claims due messages with
`UPDLOCK, READPAST`, leases them, and processes each one. Delivery is
at-least-once: an expired lease is reclaimed, a failure is retried with
exponential backoff, and a message that fails `MaxAttempts` times is marked
Failed. At processing time the handler re-reads the game: a deleted or closed
occurrence, a missing requirement, or a requirement that has already refilled
completes the message without notifying anyone. Otherwise each subscriber who
is not currently playing, is not the player who left, and has not already
been notified for this message gets one `InAppNotification` (unique on
`(sourceEventId, playerId)`, so a replay never notifies twice). The text names
the game and role only (for example "A participant spot opened in Wednesday
Basketball."), never who left or why.

#### `PUT api/v1/events/{occurrenceId}/roster/requirements/{requirementId}/subscription`

Turns on "notify me" for the caller. Idempotent. Order: no token `401`,
unlinked `403`, missing occurrence, or a requirement of another occurrence,
`404`; an existing subscription returns `200`; a closed occurrence `409`
`OccurrenceClosed`; a caller who currently holds a spot in the game `409`
`AlreadyParticipating`. **Response `201`** (created) or **`200`** (already
on): `{ occurrenceId, rosterRequirementId, subscribed: true, createdAtUtc }`.
A subscription is allowed while the requirement still has open spots; it only
matters once a later departure opens one.

#### `DELETE api/v1/events/{occurrenceId}/roster/requirements/{requirementId}/subscription`

Turns it off. Same `401`/`403`/`404` order. **Response `204`**, also when no
subscription existed.

Both subscription routes are rate limited per caller (fixed window,
`RateLimiting:Subscriptions:PermitLimit` default 30 per
`RateLimiting:Subscriptions:WindowSeconds` default 60, in memory per API
instance): **`429`** with `Retry-After`.

#### `GET api/v1/players/me/notifications?unreadOnly=false&pageSize=20&cursor=`

The caller's notifications, newest first, keyset paged on
`(createdAtUtc, id)`. `pageSize` 1 to 50 (default 20; out of range uses the
default); pass the returned `nextCursor` (opaque) for the next page; an
invalid cursor is `400`. Items: `id`, `type` (`roster.vacancy`),
`occurrenceId`, `rosterRequirementId`, `title`, `body`, `createdAtUtc`,
`readAtUtc`, `isRead`. Only the caller's own notifications are ever returned.
`401` without a token, `403` unlinked.

#### `GET api/v1/players/me/notifications/unread-count`

**Response `200`:** `{ "unreadCount": 3 }`.

#### `POST api/v1/players/me/notifications/{id}/read`

Marks one of the caller's notifications read. Idempotent (the first read time
is kept). **Response `204`**; another player's or a missing notification is
`404`.

#### `POST api/v1/players/me/notifications/{id}/opened`

**Current (TB-REFILL-002).** The caller opened the game from this
notification. Optional body `{ "via": "push" | "inApp", "clickToOpenMs": 420 }`
(`clickToOpenMs` is measured by the client from the click to the game
showing; values outside 0 to 10 minutes are ignored). Marks the notification
read and records the open time once; **`204`**, idempotent; another player's or
a missing notification is `404`. It feeds metrics only: it grants nothing and
claims nothing.

**Subscription limit.** A player may hold at most
`RefillLimits:MaxActiveOccurrenceSubscriptionsPerPlayer` (default 50)
subscriptions on games that are not completed, cancelled or archived. One more
is **`409`** with code `SubscriptionLimitReached`; re-subscribing to one already
held is still `200`. A game's subscriber list is never truncated: every
subscriber is notified.

### Browser alerts (Web Push)

**Current (TB-REFILL-002):** the same vacancy alert can also arrive as a
browser notification (RFC 8030 Web Push, RFC 8291 `aes128gcm` encryption,
RFC 8292 VAPID), for players who press **Notify me when a spot opens** in the
web client and allow notifications. It is off unless the operator configures a
VAPID key pair (`WebPush` settings, see
[deployment](deployment.md#web-push-vapid)). Push is best effort and never
blocks the in-app notification, which is always created first.

**Pipeline.** The outbox handler that creates the in-app notifications also
inserts one `PushDeliveries` row per active browser of each notified player,
in the same commit. A separate dispatcher in the API process claims due
deliveries (`UPDLOCK, READPAST`, leased like the outbox), checks that the
browser is still active and still belongs to that player and that the alert is
younger than `WebPush:TimeToLive` (30 minutes by default), encrypts the
payload, signs a VAPID token and posts it to the browser's push service.
Responses:

| Push service answer | Result |
|---|---|
| `2xx` | Accepted; the device's failure count resets. |
| `404` or `410` | That browser is gone: the delivery fails and the subscription is disabled (`Expired`). |
| `429`, `5xx`, timeout, network error | Retried with exponential backoff (honouring `Retry-After`) up to `WebPush:MaxAttempts`, then failed. |
| other `4xx` (`400`, `401`, `403`, `413`) | That delivery fails; after `WebPush:MaxConsecutiveFailures` failures in a row the subscription is disabled (`Rejected`). |

A failure affects only that browser; other browsers and the outbox are not
held up. Database effects are idempotent (one in-app notification per
`(message, player)`, one delivery per `(notification, browser)`); the push
itself is at-least-once and can, rarely, be shown twice (the service worker
uses one notification tag per game, so a repeat replaces rather than stacks).

**Payload.** Encrypted for the browser; at most a type, the notification id,
the occurrence id, the title, the body, a same-origin URL
(`/games/{occurrenceId}?n={notificationId}`), a per-game tag and the send time.
For example title "Basketball spot opened", body "A participant spot opened in
Wednesday Basketball.". Never who left, the venue address, coordinates,
distance, email, identity-provider data or the roster.

**Click.** The service worker opens or focuses the exact game URL. The page
re-reads the game; if the spot was already taken or the game was cancelled it
says so and shows the current roster. Nothing is claimed automatically: the
player presses **Join game** and the normal atomic claim decides.

#### `GET api/v1/push/config`

Anonymous. **Response `200`:** `{ "enabled": true, "vapidPublicKey": "BN…" }`
(`enabled: false` and no key when the server does not send push; the client
then hides the option).

#### `PUT api/v1/players/me/push-subscriptions`

Registers or refreshes the caller's browser. Body: the browser's
`PushSubscription.toJSON()` plus an optional `previousEndpoint`:

```json
{
  "endpoint": "https://fcm.googleapis.com/fcm/send/…",
  "expirationTime": null,
  "keys": { "p256dh": "BP…", "auth": "…" },
  "previousEndpoint": "https://fcm.googleapis.com/fcm/send/…"
}
```

The endpoint must be `https` on a known push service
(`WebPush:AllowedEndpointHosts`, default FCM, Mozilla, Apple, Windows), with no
credentials, port or fragment, so the server never posts to arbitrary hosts;
`p256dh` must be an uncompressed P-256 point and `auth` 16 bytes. Idempotent:
**`201`** when new, **`200`** when the endpoint is already registered (keys,
expiry and last-seen are refreshed; an endpoint previously registered by
another player on the same browser moves to the caller). `previousEndpoint`
(after the browser rotated its subscription) disables the old one. A player has
at most `WebPush:MaxDevicesPerPlayer` (default 10) active browsers; registering
one more disables the least recently seen. Errors: `400` with fixed text (the
submitted endpoint and keys are never echoed or logged), `401`, `403` unlinked,
**`409`** `WebPushDisabled` when the server has no VAPID identity, `429`.

**Response:** `{ id, userAgentFamily, createdAtUtc, lastSeenAtUtc,
expiresAtUtc, isActive, disabledAtUtc, disabledReason }`. Endpoints and keys are
credentials: no response ever contains them.

#### `POST api/v1/players/me/push-subscriptions/unregister`

Body `{ "endpoint": "…" }`. Deletes the caller's subscription for that
endpoint, credentials included. **`204`**, also when there was none (or it
belongs to someone else, which is left untouched).

#### `GET api/v1/players/me/push-subscriptions`

The caller's browsers, as above (no endpoints or keys).

#### `DELETE api/v1/players/me/push-subscriptions/{id}`

Deletes one of the caller's browsers (for example a lost phone). **`204`**;
another player's or a missing id is `404`.

The three mutation routes share the `RateLimiting:PushSubscriptions` limit
(default 20 per 60 seconds per caller, partitioned by token issuer and
subject): **`429`** with `Retry-After`.

**Future, not implemented:** native APNS/FCM (mobile apps), SMS and email
delivery, spot reservations or holds (`SpotReservation`), host approval,
competitive applications, waitlists and ranked matching, and an external
message bus. See the
[rapid-refill architecture](architecture/rapid-refill-platform.md#current-web-push-and-refill-operations-tb-refill-002).

---

### Venues — `api/v1/venues`

A venue is where games happen. It has ownership and a privacy level:

- `createdByPlayerId` is the player who created it through the API. Venues that
  existed before the Venue API have none (system, imported or shared venues).
- `privacyLevel` `Public` (1): a community gym, park, public court or arena; the
  address may be shown to anyone. `Private` (2): a residential driveway,
  backyard court, private club or invite-only place. "Private" never hides the
  game: it is still discoverable, but its exact street address and coordinates
  are shown only to the game's host and current participants (and to the
  venue's creator on `GET /venues/{id}`).

There is no update or delete. A venue may be shared by other hosts' games, so
moving it would move their games; a host who needs another place creates a new
venue. Nothing is geocoded: the client supplies coordinates (browser location
or manual entry).

#### `POST api/v1/venues`

Linked caller required (401 without a token, 403 when unlinked).

```json
{
  "name": "Union Park courts",
  "addressLine1": "1501 W Randolph St",
  "city": "Chicago",
  "stateOrProvince": "IL",
  "postalCode": "60607",
  "countryCode": "US",
  "latitude": 41.8849,
  "longitude": -87.6661,
  "timeZoneId": "America/Chicago",
  "venueType": 2,
  "privacyLevel": 1
}
```

| Field | Rule |
|---|---|
| `name` | Required, 1–200, not blank. For a Private venue, don't put the street address in the name: the name is shown to everyone. |
| `venueType` | Required: `Indoor` (1), `Outdoor` (2) or `Virtual` (3). |
| `privacyLevel` | Required: `Public` (1) or `Private` (2). |
| `latitude`, `longitude` | Physical venues: both required, latitude −90..90, longitude −180..180 (stored as `decimal(9,6)`). Virtual venues: must be omitted. |
| `timeZoneId` | Physical venues: required IANA id (for example `America/Chicago`; Windows ids are refused). Optional for Virtual. |
| `countryCode` | Optional ISO 3166-1 alpha-2 letters; stored upper-case. |

**Response `201`:** `VenueDto` (`isMine: true`).
**Response `400`:** Validation failure.

#### `GET api/v1/venues/{id}`

Public. A Private venue returns `isAddressMasked: true` with null
`addressLine1`, `addressLine2`, `postalCode`, `latitude` and `longitude` unless
the caller created it. **Response `404`:** not found.

### Discovery — `api/v1/discover`

#### `GET api/v1/discover/occurrences`

"Basketball near me, Wednesday around 8 PM." Live occurrences (Planned, Open,
InProgress) at **physical venues with coordinates** within a radius, closest
first. Anonymous callers are welcome; a valid linked token adds `viewer` and
unmasks Private venues of games the caller hosts or plays in.

| Parameter | Default | Rule |
|---|---|---|
| `lat` | required | −90..90 |
| `lon` | required | −180..180 |
| `radiusMiles` | 15 | > 0 and ≤ 100. Clients offer the presets 15, 25 and 50. |
| `activity` | all | The occurrence `category` code (for example `basketball`), trimmed and compared case-insensitively. Never matched against event names. |
| `fromUtc` | now | Start of the window (games whose start is ≥ `fromUtc`). |
| `toUtc` | `fromUtc` + 7 days | End of the window (start < `toUtc`); at most 30 days after `fromUtc`. |
| `openOnly` | false | Only games with `totalOpenQuantity > 0`. |
| `pageSize` | 20 | 1–50. |
| `cursor` | none | The previous page's `nextCursor`. |

**Time.** The API takes UTC boundaries only. The client turns human intent
into UTC in the searcher's zone and the API never reinterprets a game's stored
instant. Examples for a searcher in Chicago (CDT, UTC−5) on Wednesday
2026-10-14:

| Intent | `fromUtc` | `toUtc` |
|---|---|---|
| That day | `2026-10-14T05:00:00Z` | `2026-10-15T05:00:00Z` |
| That evening (5–11 PM) | `2026-10-14T22:00:00Z` | `2026-10-15T04:00:00Z` |
| Around 8 PM (7–9 PM) | `2026-10-15T00:00:00Z` | `2026-10-15T02:00:00Z` |

Each result carries `timeZoneId` (the venue's) so clients show the start in
the game's local time. There is no natural-language parsing.

**Order and paging.** Distance ascending, then `scheduledStartUtc`, then
`occurrenceId`. Keyset-paged: `nextCursor` carries the exact distance SQL
Server computed (its IEEE-754 bits, not the rounded display value), the start,
the id and the search window, so equal distances and identical starts continue
deterministically, and a search whose window defaulted to "from now" keeps the
same window on later pages. A cursor is bound to its search (point, radius,
activity, window, openOnly): reusing it with different parameters is a 400.
Games added after a page was read appear on later pages only if they sort after
the cursor.

**Response `200`:**

```json
{
  "items": [
    {
      "occurrenceId": "…",
      "seriesId": null,
      "name": "Wednesday Basketball 8 PM",
      "category": "basketball",
      "status": 1,
      "scheduledStartUtc": "2026-10-15T01:00:00Z",
      "scheduledEndUtc": "2026-10-15T03:00:00Z",
      "timeZoneId": "America/Chicago",
      "venue": {
        "venueId": "…", "name": "Union Park courts", "city": "Chicago", "stateOrProvince": "IL",
        "countryCode": "US", "addressLine1": "1501 W Randolph St", "addressLine2": null,
        "postalCode": "60607", "latitude": 41.8849, "longitude": -87.6661,
        "timeZoneId": "America/Chicago", "venueType": 2, "privacyLevel": 1,
        "distanceMiles": 2.04, "isAddressMasked": false
      },
      "roster": { "totalRequiredCount": 10, "totalSupplyCount": 9, "totalOpenQuantity": 1, "isRosterReady": false, "isFull": false },
      "viewer": { "isHost": false, "isParticipating": false, "participationStatus": null }
    }
  ],
  "nextCursor": "…"
}
```

- **Roster totals span every requirement** (for example tank 2 / healer 4 /
  dps 14): `totalRequiredCount` = Σ required; `totalSupplyCount` = current
  supply-status assignments attached to a requirement; `totalOpenQuantity` =
  Σ max(0, required − supply) per requirement; `isRosterReady` = at least one
  requirement and all satisfied; `isFull` = no requirement has an open spot.
- `viewer` is null for anonymous or unlinked callers.
- **Private venues** are masked as on the detail (no street, postal code or
  coordinates; `isAddressMasked: true`) unless the caller hosts or currently
  plays in that game. Their search point is snapped to a 0.01° grid (about
  1 km), so `distanceMiles` is approximate (to 0.1 mi) and repeated searches or
  radius probing cannot pinpoint the address.
- Never returned: email, issuer, subject, provider, tenant, other identity
  metadata, or participant lists.
- Excluded: virtual venues, venues without both coordinates (nothing is
  invented, no 0,0), games without a venue (free-text `location` only), and
  Completed, Cancelled or Archived games.

**Search location privacy.** `lat`/`lon` are a transient query: they are not
written to any table (players, identities, profiles, audit or tracking), and
the API's request log records the path without the query string. Validation
errors name the parameter without echoing its value.

**Rate limit.** Discovery is the first high-frequency public route: a fixed
window per client IP, `RateLimiting:Discovery:PermitLimit` requests (default
60) per `RateLimiting:Discovery:WindowSeconds` (default 60), in memory per API
instance. Over the limit: `429` with `Retry-After`. Behind a reverse proxy
every client shares the proxy's address unless `ForwardedHeaders` lists that
proxy (see [deployment](deployment.md#reverse-proxies-and-client-addresses));
`X-Forwarded-For` from anyone else is ignored, so a client cannot choose its
own partition. Signed-in routes partition by token issuer and subject instead,
which no header can change. The limiter never decides roster claims, which the database
guards.

**Response `400`:** invalid parameters (`errors` names the field: `lat`,
`lon`, `radiusMiles`, `activity`, `toUtc`, `pageSize` or `cursor`).
**Response `429`:** too many searches.

**Storage.** SQL Server `geography` (SRID 4326) from a persisted computed column
`Venues.SearchLocation = geography::Point(Latitude, Longitude, 4326)` (NULL for
virtual venues or missing coordinates; snapped for Private venues) with the
spatial index `SIX_Venues_SearchLocation`. The domain keeps plain decimal
coordinates. There is no H3, no worldwide search service and no address
geocoder.

### Event Series — `api/v1/event-series`

A recurring schedule ("every Tuesday at 5 PM") from which concrete event
occurrences are generated. A series stores local wall-clock intent: a local
start time, an IANA time zone, a recurrence rule and a local date range. Each
occurrence carries its own resolved UTC start/end.

Active series are kept materialized automatically 21 local days ahead (see
[Materialization](#materialization)). There is no series editing,
rescheduling or regeneration API yet, no Venue API and no geo discovery;
those are separate milestones.

#### Recurrence rule — supported subset (V0.1)

`recurrenceRule` is an RFC 5545 `RRULE` value (without the `RRULE:` prefix),
restricted to this strict subset. Keys and values are upper case.

| Component | Required | Values |
|---|---|---|
| `FREQ` | Yes | `DAILY` or `WEEKLY` |
| `INTERVAL` | No (default `1`) | Integer `1`..`52` |
| `BYDAY` | No, `WEEKLY` only | Comma-separated `MO`, `TU`, `WE`, `TH`, `FR`, `SA`, `SU` (no ordinals such as `1MO`) |

Everything else is rejected with `400`: other `FREQ` values, unknown keys,
duplicate keys or weekdays, `COUNT`, `UNTIL`, `BYHOUR`, `BYMINUTE`,
`BYSECOND`, `BYMONTH`, `BYMONTHDAY`, `BYSETPOS`, `BYYEARDAY`, `BYWEEKNO`,
`WKST`. The series' `seriesEndDate` is the recurrence bound.

Expansion, with `seriesStartDate` acting as DTSTART:

- `DAILY`: the start date, then every `INTERVAL` days.
- `WEEKLY`: weeks run Monday to Sunday; the week containing the start date
  is week 0 and every `INTERVAL`-th week is active. Active weeks produce their
  `BYDAY` weekdays (the start date's weekday when `BYDAY` is omitted), never
  before the start date.

Examples: `FREQ=WEEKLY;BYDAY=TU` (every Tuesday),
`FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH` (Tuesday and Thursday every other week),
`FREQ=DAILY;INTERVAL=3`.

#### Time zones and daylight saving

`timeZoneId` must be an IANA identifier (for example `America/New_York` or
`UTC`) that the server's built-in time zone data resolves, spelled exactly as
the IANA id. Windows ids (`Eastern Standard Time`) and different-case
spellings are rejected. The accepted id is stored as given.

- When `venueId` is supplied and the venue has a time zone, `timeZoneId` may be
  omitted (the venue's is used) or must equal the venue's exactly (otherwise
  `400`).
- Otherwise `timeZoneId` is required.

Each occurrence is the local date plus `localStartTime` in the series time
zone, converted to UTC per date, so Tuesday 5 PM stays 5 PM local across DST.

- **Spring-forward gap** (the local time does not exist): the occurrence moves
  to the first valid local instant after the gap (02:30 on a US spring-forward
  day becomes 03:00 local). It is never skipped.
- **Fall-back overlap** (the local time happens twice): the standard-time
  instant is used (01:30 on a US fall-back day is 01:30 standard time).

#### Materialization

Creating a series also creates its occurrences for local dates from
`seriesStartDate` through `seriesStartDate + 20 days` (21 local days), or
through `seriesEndDate` if earlier, in the same database transaction as the
series. Each occurrence copies the series' name, description, category, tags,
team, host, venue and `maxParticipants`, and starts `Planned`, not detached,
with no participants and `scheduledEndUtc = scheduledStartUtc +
durationMinutes`. `OccurrenceIndex` (stored, not exposed) is the zero-based
position in the series' recurrence from its start date.

After that, a background worker in the API keeps every `Active` series
materialized through **today + 20 days, where "today" is the current date in
the series' own time zone** (21 local calendar days), bounded by
`seriesEndDate`. It runs one pass when the application starts and then about
once an hour (`EventSeriesMaterialization:Interval`, default `01:00:00`;
`EventSeriesMaterialization:Enabled` turns it off), reading active series in
batches of 100 (`EventSeriesMaterialization:BatchSize`). Paused, cancelled,
completed and ended series are left alone. A failure in one series is logged
and does not stop the others.

Each series stores a checkpoint, `MaterializedThroughLocalDate` (not exposed):
the local date through which its recurrence has been evaluated. The worker
resumes the day after it, never before the series start and never before
today, so past dates are not filled in. Series created before the checkpoint
existed have none and are reconciled on the first pass. Occurrences created in
a pass and the new checkpoint commit in one transaction that also checks the
series' row version, so nothing is created for a series that was cancelled
while the pass was running, and a pass that fails leaves the checkpoint where
it was.

The unique index `UX_Events_SeriesId_ScheduledStartUtc` (filtered to rows
with a series) guarantees a series never has two occurrences at the same
instant; materialization inserts only missing occurrences (also skipping a
position already taken by a moved, detached occurrence) and is safe to repeat
or to run on several application instances at once.

#### `POST api/v1/event-series`

Creates a series and its initial occurrences. Requires a linked caller
identity; `hostId` is the resolved internal `Player.Id`. If `teamId` is
supplied, the same rules as team event creation apply: the team must exist
(`404`), the caller must own it (`403`), and its `lifecycleStatus` must be
`Active` (`409`). If `venueId` is supplied, the venue must exist (`404`).

**Request body:**

```json
{
  "name": "Tuesday pickup",
  "description": "Bring both shirts",
  "category": "Basketball",
  "tags": "pickup,indoor",
  "teamId": null,
  "venueId": null,
  "localStartTime": "17:00:00",
  "durationMinutes": 90,
  "timeZoneId": "America/New_York",
  "recurrenceRule": "FREQ=WEEKLY;BYDAY=TU",
  "seriesStartDate": "2026-10-13",
  "seriesEndDate": "2026-12-15",
  "maxParticipants": 12
}
```

| Field | Rules |
|---|---|
| `name` | Required, 1..200 characters |
| `localStartTime` | Required, local wall-clock time |
| `durationMinutes` | Required, 1..10080 |
| `recurrenceRule` | Required, V0.1 subset above |
| `seriesStartDate` | Required; not before the current date in the series time zone |
| `seriesEndDate` | Optional (null = indefinite); on or after `seriesStartDate` |
| `maxParticipants` | Required, 1..100000 |

- **Response `201`:** Created `EventSeriesDto` (with `Location` header).
- **Response `400`:** Validation failure (rule, time zone, dates, ranges).
- **Response `401`:** No valid JWT provided.
- **Response `403`:** Caller has no linked player or does not own the supplied team.
- **Response `404`:** Supplied team or venue not found.
- **Response `409`:** Supplied team is inactive or disbanded.

---

#### `GET api/v1/event-series/{id}`

Public. Returns the series template; occurrences are not embedded.

- **Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000010",
  "name": "Tuesday pickup",
  "description": "Bring both shirts",
  "category": "Basketball",
  "tags": "pickup,indoor",
  "teamId": null,
  "hostId": "00000000-0000-0000-0000-000000000001",
  "venueId": null,
  "localStartTime": "17:00:00",
  "durationMinutes": 90,
  "timeZoneId": "America/New_York",
  "recurrenceRule": "FREQ=WEEKLY;BYDAY=TU",
  "seriesStartDate": "2026-10-13",
  "seriesEndDate": "2026-12-15",
  "status": "Active",
  "maxParticipants": 12,
  "createdAtUtc": "2026-10-05T12:00:00Z",
  "updatedAtUtc": null
}
```

`EventSeriesStatus` values: `Active`, `Paused`, `Cancelled`, `Completed`.

- **Response `404`:** Series not found.

---

#### `GET api/v1/event-series/{id}/occurrences`

Public. Returns the series' occurrences as `PaginatedResult<EventDto>`, ordered
by `scheduledStartUtc` ascending. Supports `page` (default 1) and `pageSize`
(default 20, max 100).

- **Response `200`:** `PaginatedResult<EventDto>`
- **Response `404`:** Series not found.

---

#### `DELETE api/v1/event-series/{id}`

Logically cancels the series; nothing is deleted. Only the series host may
cancel. In one transaction the series' `status` becomes `Cancelled` and every
occurrence of it that starts in the future and is not detached becomes
`Cancelled`. Past and in-progress occurrences, and detached occurrences
(including any occurrence edited through `PUT api/v1/events/{id}`), are left
unchanged. Once cancelled, the series gains no new occurrences.

- **Response `204`:** Cancelled.
- **Response `401`:** No valid JWT provided.
- **Response `403`:** Caller has no linked player or is not the series host.
- **Response `404`:** Series not found.
- **Response `409`:** Series has no host (orphaned), is already cancelled, or
  was changed concurrently (the losing request of two simultaneous
  cancellations, or a cancellation that raced with the materialization
  worker; retrying succeeds).

---

### Roster Imports — `api/v1/rosterimports`

#### `GET api/v1/rosterimports/{id}`

Returns a single roster import record by ID. Requires authentication and is
visible only to its original importer; the response includes uploaded
`RawData`.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000005",
  "sourceName": "TeamSpreadsheet",
  "sourceType": "CSV",
  "rawData": "Name,Role\nstriker99,Tank",
  "isProcessed": false,
  "processedAtUtc": null,
  "processingNotes": null,
  "importedByUserId": "00000000-0000-0000-0000-000000000001",
  "createdAtUtc": "2025-01-01T00:00:00Z"
}
```

**Response `404`:** Roster import not found.

---

#### `GET api/v1/rosterimports`

Returns a paginated list of the caller's own roster imports. Requires
authentication and a linked player identity.

**Query parameters:**

| Parameter    | Type | Description                          |
|--------------|------|--------------------------------------|
| `page`       | int  | Page number (default: 1)             |
| `pageSize`   | int  | Page size (default: 20)              |
| `isProcessed`| bool | Filter by processed status           |

**Response `200`:** `PaginatedResult<RosterImportDto>`

---

#### `POST api/v1/rosterimports`

Creates a new roster import record. Does not process it immediately.
Requires a linked caller identity; `ImportedByUserId` is set to the resolved
internal `Player.Id`, not copied from a token claim.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Request body:**

```json
{
  "sourceName": "TeamSpreadsheet",
  "sourceType": "CSV",
  "rawData": "Name,Role\nstriker99,Tank\ngoalie01,Support"
}
```

**Response `201`:** Created `RosterImportDto`.  
**Response `400`:** Validation failure.

---

#### `PUT api/v1/rosterimports/{id}/process`

Processes a roster import. Parses CSV `rawData` (format: `Name,Role,Notes`),
creates `Player` records for any unrecognized usernames, and marks the import
as processed. Can only be processed once.

Requires `Authorization: Bearer <token>`.

**Headers:**

| Header | Type | Description |
|---|---|---|
| `Authorization` | string | `Bearer <jwt-token>` |

**Response `200`:** Updated `RosterImportDto` with `processingNotes`.  
**Response `401`:** No valid JWT provided.  
**Response `403`:** Authenticated caller is not the original importer.  
**Response `404`:** Roster import not found.  
**Response `409`:** Import already processed, or import has no owner (orphaned); contact an administrator.

---

#### `DELETE api/v1/rosterimports/{id}`

Deletes a roster import record.

**Response `204`:** Deleted.  
**Response `401`:** No valid JWT provided.  
**Response `403`:** Authenticated caller is not the original importer.  
**Response `404`:** Roster import not found.  
**Response `409`:** Roster import has no owner (orphaned); contact an administrator.

---

### Health — `/healthz/live` and `/healthz/ready`

Anonymous, not rate limited, and meant for orchestrators and load balancers. Both answer JSON
with the overall status, each check's status and the build metadata, and never exception text:

```json
{ "status": "Healthy", "version": "1.0.0", "commit": "<git sha>", "checks": { "database": "Healthy", "environment": "Healthy", "configuration": "Healthy" } }
```

#### `GET /healthz/live`

Liveness. Runs no check and touches no dependency: `200` while the process serves requests.

#### `GET /healthz/ready`

Readiness. `200` only when every check is Healthy, else `503`:

| Check | Healthy when |
|---|---|
| `database` | The database answers and every migration in this build is applied (a database with additional, newer migrations is still Healthy, for rollbacks). |
| `environment` | In QA/Production, the database is stamped for this environment (`database stamp-environment`). Always Healthy in Development and LocalQA. |
| `configuration` | The deployment configuration rules hold (the same rules are enforced at startup). |

Web Push is not part of readiness.

`GET /health` and `GET /health/ready` remain as plain-text (`Healthy` / `Unhealthy`) aliases of
the same two checks.

---

## Using the Postman Collection

1. Import `docs/postman/TeamBuilder.postman_collection.json` into Postman.
2. Import `docs/postman/TeamBuilder.local.postman_environment.json` and select
   it as the active environment.
3. Update `baseUrl` in the environment to match the port shown when you run the
   API locally (e.g., `https://localhost:7123`).
4. Issue a local dev token and paste it into the `token` environment variable:
   ```powershell
   cd src/TeamBuilder.Api
   dotnet user-jwts create --audience teambuilder-api --claim sub=local-user-123
   ```
5. Replace placeholder GUIDs (`playerId`, `teamId`, etc.) with real IDs from
   previous responses or your local database.

---

## Known Limitations

| Limitation | Detail |
|---|---|
| **Authorization is endpoint-specific** | Public discovery is limited to teams, events, and public player profiles. Join-request and roster-import reads are restricted; write and profile mutation authorization is resource-specific as described above. |
| **Data annotations** | All request DTOs have `[Required]`, `[StringLength]`, `[Range]`, `[EmailAddress]`, and `[EnumDataType]` annotations where appropriate. Missing or invalid fields return `400 ValidationProblemDetails`. |
| **EF Core migrations** | An `InitialCreate` migration exists. Run `dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api` before first local run. |
| **RosterImport CSV parsing is basic** | The parser skips the header and creates players from column 0. It does not associate entries with specific events or teams. |
| **Readiness requires a migrated database** | Running locally without a migrated database makes `/healthz/ready` report unhealthy. `/healthz/live` always returns `200`. |

---

## Recommended Future API Improvements

See [deployment-next-steps.md](deployment-next-steps.md) for historical hosting
and deployment recommendations; verify them against current source before use.
Short-term API-only improvements:

1. Require authentication and enforce appropriate player-level authorization
   on player create/update/delete routes.
2. Verify the selected identity provider and its deployed configuration;
   provider-side state is not established by this repository.
