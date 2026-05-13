# TeamBuilder API

## Overview

TeamBuilder is an API-first, frontend-agnostic platform for building, hosting,
joining, maintaining, and refilling teams. The ASP.NET Core Web API is the
middle layer between any client-side frontend and the backend data platform.

- **Base path:** `api/v1`
- **Format:** JSON (request and response)
- **Authentication:** JWT Bearer required on write endpoints; read and health endpoints are public.
  See [Authentication](#authentication) for details and local dev token setup.
- **Persistence:** EF Core Code First targeting Azure SQL Server.
- **Health endpoints:** `GET /health` (liveness), `GET /health/ready` (readiness)

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
curl https://localhost:<port>/health

# Readiness: is the database reachable?
curl https://localhost:<port>/health/ready
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

> **Status: Phase 3 — Ownership authorization enforced on team mutation endpoints.**
> See [`docs/auth-plan.md`](auth-plan.md) for the full phased implementation plan.

### Ownership authorization (Phase 3)

Team mutation endpoints enforce ownership in addition to authentication.
Only the user whose `sub` claim matches the team's `OwnerId` may update or
delete a team. Authenticated users who do not own the team receive
`403 Forbidden`. Unauthenticated requests continue to receive `401 Unauthorized`.

| Method | Path | Ownership check |
|---|---|---|
| `PUT` | `/api/v1/teams/{id}` | `sub` claim must match `OwnerId`. |
| `DELETE` | `/api/v1/teams/{id}` | `sub` claim must match `OwnerId`. |

### Protected endpoints (require `Authorization: Bearer <token>`)

The following write endpoints require a valid JWT Bearer token. Requests
without a valid token receive `401 Unauthorized`.

| Method | Path | Notes |
|---|---|---|
| `POST` | `/api/v1/teams` | Sets `OwnerId` from `sub` claim. |
| `PUT` | `/api/v1/teams/{id}` | Requires authentication; owner only (see above). |
| `DELETE` | `/api/v1/teams/{id}` | Requires authentication; owner only (see above). |
| `POST` | `/api/v1/teams/{teamId}/members/{playerId}/leave` | Requires authentication. |
| `POST` | `/api/v1/joinrequests` | Sets `PlayerId` from `sub` claim. |
| `PUT` | `/api/v1/joinrequests/{id}/process` | Identifies processing user from `sub` claim. |
| `POST` | `/api/v1/events` | Sets `HostId` from `sub` claim. |
| `PUT` | `/api/v1/events/{id}` | Requires authentication. |
| `DELETE` | `/api/v1/events/{id}` | Requires authentication. |
| `POST` | `/api/v1/rosterimports` | Sets `ImportedByUserId` from `sub` claim. |
| `PUT` | `/api/v1/rosterimports/{id}/process` | Requires authentication. |
| `DELETE` | `/api/v1/rosterimports/{id}` | Requires authentication. |

### Anonymous endpoints (no token required)

| Method | Path |
|---|---|
| `GET` | `/health` |
| `GET` | `/health/ready` |
| `GET` | `/api/v1/teams`, `/api/v1/teams/{id}` |
| `GET` | `/api/v1/joinrequests/{id}`, `/api/v1/joinrequests/teams/{teamId}`, `/api/v1/joinrequests/players/{playerId}` |
| `GET` | `/api/v1/events`, `/api/v1/events/{id}` |
| `GET` | `/api/v1/rosterimports`, `/api/v1/rosterimports/{id}` |
| `GET` | `/api/v1/players`, `/api/v1/players/{id}` |
| `GET` | `/swagger` (Development only) |

### `X-User-Id` transition behavior

The `X-User-Id` header fallback is still wired in `ClaimsCurrentUserContext`
but is no longer sufficient to call any protected write endpoint — the
middleware gate returns `401` before the controller sees the header. The header
remains available as a last-resort fallback for unauthenticated contexts.

| Scenario | `UserId` value |
|---|---|
| Valid JWT with a `sub` GUID claim | GUID from the `sub` claim |
| Invalid / expired JWT on a **protected** endpoint | `401 Unauthorized` |
| Invalid / expired JWT on a **public** endpoint | `Guid.Empty` (falls through to `X-User-Id` header) |
| No JWT on a **protected** endpoint | `401 Unauthorized` |
| No JWT, valid `X-User-Id` on public endpoint | GUID from the header |
| No JWT, missing / invalid `X-User-Id` on public endpoint | `Guid.Empty` |

```http
POST /api/v1/teams HTTP/1.1
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/json
```

### Local development — issuing tokens with `dotnet user-jwts`

```powershell
cd src/TeamBuilder.Api

# Issue a dev token (stores signing key in user-secrets automatically)
dotnet user-jwts create --audience teambuilder-api --claim sub=<your-player-guid>
```

Copy the printed token into Postman as `Authorization: Bearer <token>`.

To align with TeamBuilder's `Jwt:SigningKey` config path, copy the generated
key into user-secrets:

```powershell
dotnet user-secrets set "Jwt:SigningKey" "<key-from-user-jwts>"
dotnet user-secrets set "Jwt:Issuer"     "dotnet-user-jwts"
```

See [`docs/auth-plan.md`](auth-plan.md) for all configuration keys and the
full transition-behavior table.

---

## Error Responses

All API errors are returned as `application/problem+json` using the
[RFC 9457 ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) envelope.

### ProblemDetails envelope

| Field      | Type   | Description                                                      |
|------------|--------|------------------------------------------------------------------|
| `type`     | string | URI reference identifying the problem type (may be omitted).     |
| `title`    | string | Short, human-readable summary of the problem type.               |
| `status`   | int    | HTTP status code.                                                |
| `detail`   | string | Human-readable explanation specific to this occurrence.          |
| `traceId`  | string | ASP.NET Core request trace ID. Include this when reporting bugs. |

### Status code mappings

| Scenario                                    | Status | Exception / source              |
|---------------------------------------------|--------|---------------------------------|
| Model validation failure (data annotations) | `400`  | `ValidationProblemDetails`      |
| Invalid argument (business rule)            | `400`  | `ArgumentException`             |
| Unauthenticated request on protected route  | `401`  | Auth middleware                 |
| Authenticated but not resource owner        | `403`  | Ownership check in controller   |
| Resource not found                          | `404`  | `KeyNotFoundException`          |
| Conflict (duplicate or invalid state)       | `409`  | `InvalidOperationException`     |
| Unexpected server error                     | `500`  | Unhandled exception             |

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
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred. Please try again later.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

---

## Endpoint Inventory

### Players — `api/v1/players`

#### `GET api/v1/players/{id}`

Returns a single player by ID.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "username": "striker99",
  "email": "striker99@example.com",
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

**Response `200`:** Same shape as `GET /players/{id}`.  
**Response `404`:** Player not found.

---

#### `GET api/v1/players`

Returns a paginated list of players.

**Query parameters:**

| Parameter | Type   | Description              |
|-----------|--------|--------------------------|
| `page`    | int    | Page number (default: 1) |
| `pageSize`| int    | Page size (default: 20)  |
| `region`  | string | Filter by region         |

**Response `200`:** `PaginatedResult<PlayerDto>`

---

#### `POST api/v1/players`

Creates a new player. Username must be unique.

**Request body:**

```json
{
  "username": "striker99",
  "email": "striker99@example.com",
  "displayName": "Striker",
  "bio": "Competitive FPS player",
  "region": "NA",
  "avatarUrl": "https://example.com/avatar.png"
}
```

**Response `201`:** Created `PlayerDto` with `Location` header.  
**Response `400`:** Validation failure or duplicate username.

---

#### `PUT api/v1/players/{id}`

Updates an existing player. Only non-null fields are applied.

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

---

#### `DELETE api/v1/players/{id}`

Deletes a player.

**Response `204`:** Deleted.  
**Response `404`:** Player not found.

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
  "status": "Recruiting",
  "maxMembers": 10,
  "currentMemberCount": 3,
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
| `status`   | TeamStatus | Filter by status         |

`TeamStatus` values: `Recruiting`, `Active`, `Full`, `Inactive`, `Disbanded`

**Response `200`:** `PaginatedResult<TeamDto>`

---

#### `POST api/v1/teams`

Creates a new team. Requires `Authorization: Bearer <token>`. Sets `OwnerId` from the JWT `sub` claim.

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
  "status": "Active",
  "maxMembers": 12,
  "region": "EU",
  "category": "FPS",
  "tags": "fps,competitive,ranked"
}
```

**Response `200`:** Updated `TeamDto`.  
**Response `404`:** Team not found.  
**Response `400`:** Validation failure.

---

#### `DELETE api/v1/teams/{id}`

Deletes a team.

**Response `204`:** Deleted.  
**Response `404`:** Team not found.

---

#### `POST api/v1/teams/{teamId}/members/{playerId}/leave`

Removes a player from a team. Marks the `TeamMember` record as inactive.
Decrements `CurrentMemberCount`. If the team was `Full` and now has capacity,
the status transitions to `Recruiting`.

**Response `204`:** Member removed.  
**Response `404`:** Active team member not found.

---

### Join Requests — `api/v1/joinrequests`

#### `GET api/v1/joinrequests/{id}`

Returns a single join request by ID. Includes team and player usernames.

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

Returns paginated join requests for a team.

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

Returns paginated join requests for a player.

**Query parameters:** Same as `GET /joinrequests/teams/{teamId}`.

**Response `200`:** `PaginatedResult<JoinRequestDto>`

---

#### `POST api/v1/joinrequests`

Submits a join request. Only one pending request per player per team is allowed.
Requires `Authorization: Bearer <token>`. Sets `PlayerId` from the JWT `sub` claim.

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

**Response `201`:** Created `JoinRequestDto`.  
**Response `400`:** Validation failure or duplicate pending request.

---

#### `PUT api/v1/joinrequests/{id}/process`

Processes (approves, rejects, or cancels) a pending join request.
Only `Pending` requests can be processed. Approving a request:

- Creates a new `TeamMember` record.
- Increments `Team.CurrentMemberCount`.
- Sets team status to `Full` if at capacity.

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
**Response `404`:** Join request not found.  
**Response `400`:** Request is not in `Pending` status.

---

### Events — `api/v1/events`

#### `GET api/v1/events/{id}`

Returns a single event by ID. Includes team name and host username.

**Response `200`:**

```json
{
  "id": "00000000-0000-0000-0000-000000000004",
  "name": "Spring Championship",
  "description": "Annual spring tournament",
  "eventDateUtc": "2025-04-01T18:00:00Z",
  "status": "Planned",
  "category": "FPS",
  "tags": "fps,tournament",
  "location": "Online",
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

#### `GET api/v1/events`

Returns a paginated list of events, ordered by `EventDateUtc` ascending.

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

Creates a new event. Requires `Authorization: Bearer <token>`. Sets `HostId` from the JWT `sub` claim.

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

**Response `201`:** Created `EventDto`.  
**Response `400`:** Validation failure.

---

#### `PUT api/v1/events/{id}`

Updates an existing event. Only non-null fields are applied.

**Request body:**

```json
{
  "name": "Spring Championship 2025",
  "status": "Open",
  "maxParticipants": 128
}
```

**Response `200`:** Updated `EventDto`.  
**Response `404`:** Event not found.  
**Response `400`:** Validation failure.

---

#### `DELETE api/v1/events/{id}`

Deletes an event.

**Response `204`:** Deleted.  
**Response `404`:** Event not found.

---

### Roster Imports — `api/v1/rosterimports`

#### `GET api/v1/rosterimports/{id}`

Returns a single roster import record by ID.

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

Returns a paginated list of roster imports.

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
Requires `Authorization: Bearer <token>`. Sets `ImportedByUserId` from the JWT `sub` claim.

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
**Response `404`:** Roster import not found.  
**Response `400`:** Already processed.

---

#### `DELETE api/v1/rosterimports/{id}`

Deletes a roster import record.

**Response `204`:** Deleted.  
**Response `404`:** Roster import not found.

---

### Health — `/health` and `/health/ready`

#### `GET /health`

Liveness check. Returns `Healthy` as long as the API process is running.
No external dependencies are checked. Use this to verify the process is alive.

**Response `200`:** Healthy.

---

#### `GET /health/ready`

Readiness check. Verifies that external dependencies are reachable before
marking the API as ready to serve traffic. The check is registered under the
name `TeamBuilderDb` and verifies database connectivity using the configured
`TeamBuilderSql` connection string.

**Response `200`:** Healthy — database is reachable.  
**Response `503`:** Unhealthy — database is unreachable.

---

## Using the Postman Collection

1. Import `docs/postman/TeamBuilder.postman_collection.json` into Postman.
2. Import `docs/postman/TeamBuilder.local.postman_environment.json` and select
   it as the active environment.
3. Update `baseUrl` in the environment to match the port shown when you run the
   API locally (e.g., `https://localhost:7123`).
4. Replace placeholder GUIDs (`playerId`, `teamId`, etc.) with real IDs from
   previous responses or your local database.

---

## Known Limitations

| Limitation | Detail |
|---|---|
| **No ownership authorization** | Any authenticated caller can modify any resource (e.g., update another user's team). Role/policy-based authorization is planned for a future phase — see [`docs/auth-plan.md`](auth-plan.md). |
| **Data annotations** | All request DTOs have `[Required]`, `[StringLength]`, `[Range]`, `[EmailAddress]`, and `[EnumDataType]` annotations where appropriate. Missing or invalid fields return `400 ValidationProblemDetails`. |
| **EF Core migrations** | An `InitialCreate` migration exists. Run `dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api` before first local run. |
| **RosterImport CSV parsing is basic** | The parser skips the header and creates players from column 0. It does not associate entries with specific events or teams. |
| **Health check requires live SQL** | Running locally without a database will cause `/health/ready` to report unhealthy. `/health` (liveness) always returns `200`. |

---

## Recommended Future API Improvements

See [deployment-next-steps.md](deployment-next-steps.md) for the full list.
Short-term API-only improvements:

1. Add ownership/role authorization policies (team owner, admin roles).
2. Select and integrate an identity provider (Azure AD B2C, Auth0, etc.).
3. Remove `X-User-Id` header fallback once all callers have migrated to JWT.
