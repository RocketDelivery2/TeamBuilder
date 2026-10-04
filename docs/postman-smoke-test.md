# TeamBuilder Local Postman Smoke Test Guide

Use this guide to start the API locally and manually verify all endpoints
using the included Postman collection.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [SQL Server LocalDB](https://learn.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb)
  (included with Visual Studio; run `sqllocaldb info` to confirm it is available)
- [Postman](https://www.postman.com/downloads/) (desktop app recommended)
- Developer HTTPS certificate trusted locally (`dotnet dev-certs https --trust`)

---

## Step 1 — Verify the local connection string

`src/TeamBuilder.Api/appsettings.Development.json` contains:

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=(localdb)\\mssqllocaldb;Database=TeamBuilderDev;Trusted_Connection=True;MultipleActiveResultSets=true"
  }
}
```

This targets the **LocalDB** instance named `mssqllocaldb` and a database
called `TeamBuilderDev`. It does **not** touch any production or QA database.

Do not commit real connection strings or credentials.

---

## Step 2 — Create the local database (EF Core)

> **Note:** An `InitialCreate` migration exists at
> `src/TeamBuilder.Infrastructure/Persistence/Migrations/`. Apply it once
> before the first local run.

```bash
# From the repo root
dotnet tool install --global dotnet-ef   # if not already installed

dotnet ef database update \
  --project src/TeamBuilder.Infrastructure \
  --startup-project src/TeamBuilder.Api
```

This creates the `TeamBuilderDev` LocalDB database and applies the full schema.

---

## Step 3 — Start the API

```bash
cd src/TeamBuilder.Api
dotnet run
```

Expected console output:

```
Now listening on: https://localhost:7178
Now listening on: http://localhost:5076
Application started. Press Ctrl+C to shut down.
```

> If the port differs, note the actual HTTPS port and update the
> `baseUrl` variable in the Postman environment (see Step 4).

Swagger UI is available at `https://localhost:7178/swagger` while the API
is running in the Development environment.

---

## Step 4 — Import the Postman collection and environment

1. Open Postman.
2. Click **Import** and select both files:
   - `docs/postman/TeamBuilder.postman_collection.json`
   - `docs/postman/TeamBuilder.local.postman_environment.json`
3. In the top-right environment selector, choose **TeamBuilder Local**.
4. Verify `baseUrl` is `https://localhost:7178`. If your API started on a
   different port, click the environment name → edit `baseUrl` to match.
5. Issue a local dev token and paste it into the `token` variable:

   ```powershell
   cd src/TeamBuilder.Api
   dotnet user-jwts create --audience teambuilder-api --claim sub=local-user-123
   ```

   Copy the printed token into the `token` variable in the **TeamBuilder Local**
   environment. Protected requests inherit it as a bearer token; public
   discovery requests send no token.
   `Authorization: Bearer {{token}}`.

---

## Step 6 — Recommended smoke-test request order

Run requests in this order. Each step captures an ID needed by the next.

| # | Request | Folder | Notes |
|---|---------|--------|-------|
| 1 | `GET /health` | Health | Expect `200 Healthy`. This is the liveness check; the process is running. |
| 2 | `GET /health/ready` | Health | Expect `200 Healthy`. If `503`, the database is not reachable — re-run the migration from Step 2. |
| 3 | `POST /api/v1/players/me` | Players | With a valid JWT in `{{token}}`, onboards the caller. Copy `id` from the response into the `playerId` environment variable. |
| 4 | `GET /api/v1/players` | Players | Public discovery; verify the player appears without exposing Email. |
| 5 | `GET /api/v1/players/{{playerId}}` | Players | Public lookup by ID; the response omits Email. |
| 6 | `POST /api/v1/teams` | Teams | Uses the caller's bearer token. Copy `id` from the response into `teamId`. |
| 7 | `GET /api/v1/teams` | Teams | Verify the team appears in the list. |
| 8 | `GET /api/v1/teams/{{teamId}}` | Teams | Verify `ownerUsername` is populated. |
| 9 | `POST /api/v1/joinrequests` | Join Requests | Uses the applicant's bearer token. Copy `id` into `joinRequestId`. |
| 10 | `POST /api/v1/joinrequests` (duplicate) | Join Requests | Repeat request 9 with the same `token` and `teamId`. Expect `409 Conflict` with `application/problem+json`. Verify `status: 409` and a `detail` message in the response body. |
| 11 | `PUT /api/v1/joinrequests/{{joinRequestId}}/process` | Join Requests | Caller must own the team. Use body `{"status":"Approved"}`. Expect `200`. |
| 12 | `POST /api/v1/events` | Events | Caller must own the associated `teamId`. Set it to `{{teamId}}` and copy `id` into `eventId`. |
| 13 | `GET /api/v1/events` | Events | Verify the event appears in the list. |
| 14 | `POST /api/v1/rosterimports` | Roster Imports | Uses the importer's bearer token. Copy `id` into `rosterImportId`. |
| 15 | `GET /api/v1/rosterimports` | Roster Imports | Requires the importer's token; verify the caller's import appears. |

Join-request reads require authentication: the applicant can read their own
requests, while the relevant team owner can read requests for that team.
Roster-import reads require the original importer's identity because details
include uploaded data. Player and team/event discovery reads remain public;
public player responses omit Email.

### Copying IDs into Postman environment variables

After each `POST` that returns a created resource:

1. In the Postman response body, copy the `id` field value.
2. Click the **TeamBuilder Local** environment in the top-right.
3. Find the matching variable (e.g., `playerId`, `teamId`) and paste the value
   into the **Current value** column.

---

## Expected status codes

All error responses use `Content-Type: application/problem+json` and follow
the ProblemDetails envelope. See [Error Responses](api.md#error-responses) in
`docs/api.md` for full field descriptions and example JSON bodies.

| Scenario | Expected |
|---|---|
| `GET /health` — process is running | `200 Healthy` |
| `GET /health/ready` — database reachable | `200 Healthy` |
| `GET /health/ready` — database not reachable | `503 Unhealthy` |
| `POST` — valid payload | `201 Created` with `Location` header |
| `POST` — duplicate pending join request | `409 Conflict` — `application/problem+json` with `detail` |
| `POST` — invalid/missing required field | `400 Bad Request` — `application/problem+json` with `errors` dictionary |
| `GET /{id}` — resource exists | `200 OK` |
| `GET /{id}` — resource not found | `404 Not Found` — `application/problem+json` with `detail` |
| `PUT /{id}/process` — valid state transition | `200 OK` |
| `PUT /{id}/process` — already processed | `409 Conflict` — `application/problem+json` with `detail` |
| `DELETE /{id}` — resource exists | `204 No Content` |
| `DELETE /{id}` — resource not found | `404 Not Found` — `application/problem+json` with `detail` |

---

## Known limitations

| Limitation | Impact |
|---|---|
| **JWT required for write endpoints** | Protected `POST`, `PUT`, `DELETE` requests return `401` without a valid token. Issue a dev token with `dotnet user-jwts` and set it in the `token` environment variable (see Step 5). |
| **EF Core migrations must be applied** | Run `dotnet ef database update` before the first local run (see Step 2). |
| **Health check requires live LocalDB** | `/health/ready` returns `503` if LocalDB is not running. Start it with `sqllocaldb start mssqllocaldb`. `/health` (liveness) always returns `200`. |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `GET /health/ready` returns `503` | Run `sqllocaldb start mssqllocaldb` then retry. If the database does not exist, run the `dotnet ef database update` command from Step 2. |
| SSL certificate error in Postman | Run `dotnet dev-certs https --trust` and restart Postman. Alternatively, disable SSL verification in Postman settings (not recommended for production). |
| `Connection refused` on port 7178 | Check the console output for the actual port and update `baseUrl` in the Postman environment. |
| `500 Internal Server Error` on all requests | The database schema likely does not exist. Apply migrations (Step 2). |
| Postman shows `Could not send request` | Confirm the API is running (`dotnet run` output shows `Now listening on:`). |
