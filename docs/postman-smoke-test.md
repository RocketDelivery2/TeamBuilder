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

---

## Step 5 — Recommended smoke-test request order

Run requests in this order. Each step captures an ID needed by the next.

| # | Request | Folder | Notes |
|---|---------|--------|-------|
| 1 | `GET /health` | Health | Expect `200 Healthy`. If `503`, the database is not reachable — re-run the migration from Step 2. |
| 2 | `POST /api/v1/players` | Players | Creates a player. Copy `id` from the response into the `playerId` environment variable. |
| 3 | `GET /api/v1/players` | Players | Verify the player appears in the paginated list. |
| 4 | `GET /api/v1/players/{{playerId}}` | Players | Verify the player can be fetched by ID. |
| 5 | `POST /api/v1/teams` | Teams | Set `X-User-Id` header to `{{playerId}}`. Copy `id` from the response into `teamId`. |
| 6 | `GET /api/v1/teams` | Teams | Verify the team appears in the list. |
| 7 | `GET /api/v1/teams/{{teamId}}` | Teams | Verify `ownerUsername` is populated. |
| 8 | `POST /api/v1/joinrequests` | Join Requests | Set `X-User-Id` to `{{playerId}}`. Copy `id` into `joinRequestId`. |
| 9 | `POST /api/v1/joinrequests` (duplicate) | Join Requests | Repeat request 8 with the same `X-User-Id` and `teamId`. Expect `409 Conflict` with `application/problem+json`. Verify `status: 409` and a `detail` message in the response body. |
| 10 | `PUT /api/v1/joinrequests/{{joinRequestId}}/process` | Join Requests | Set `X-User-Id`. Use body `{"status":"Approved"}`. Expect `200`. |
| 11 | `POST /api/v1/events` | Events | Set `X-User-Id` to `{{playerId}}`. Set `teamId` in the body to `{{teamId}}`. Copy `id` into `eventId`. |
| 12 | `GET /api/v1/events` | Events | Verify the event appears in the list. |
| 13 | `POST /api/v1/rosterimports` | Roster Imports | Set `X-User-Id`. Copy `id` into `rosterImportId`. |
| 14 | `GET /api/v1/rosterimports` | Roster Imports | Verify the import record appears. |

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
| `GET /health` — database reachable | `200 Healthy` |
| `GET /health` — database not reachable | `503 Unhealthy` |
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
| **No authentication** | Any value works for `X-User-Id`. No request will be rejected for auth reasons. |
| **No authorization** | Any caller can read or modify any resource. |
| **EF Core migrations must be applied** | Run `dotnet ef database update` before the first local run (see Step 2). |
| **Health check requires live LocalDB** | `/health` returns `503` if LocalDB is not running. Start it with `sqllocaldb start mssqllocaldb`. |
| **No data annotations on DTOs** | Model validation is minimal. Sending an empty body will often succeed or produce a generic error. |
| **`X-User-Id` is optional on some endpoints** | If omitted, `Guid.Empty` is used as the caller identity. This does not cause an error but may produce unexpected data (e.g., `ownerId = 00000000-0000-0000-0000-000000000000`). |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `GET /health` returns `503` | Run `sqllocaldb start mssqllocaldb` then retry. If the database does not exist, run the `dotnet ef database update` command from Step 2. |
| SSL certificate error in Postman | Run `dotnet dev-certs https --trust` and restart Postman. Alternatively, disable SSL verification in Postman settings (not recommended for production). |
| `Connection refused` on port 7178 | Check the console output for the actual port and update `baseUrl` in the Postman environment. |
| `500 Internal Server Error` on all requests | The database schema likely does not exist. Apply migrations (Step 2). |
| Postman shows `Could not send request` | Confirm the API is running (`dotnet run` output shows `Now listening on:`). |
