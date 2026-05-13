# TeamBuilder Authentication Implementation Plan

This document captures the current temporary user-context behavior, identifies
affected endpoints, and defines a phased plan for replacing the placeholder
**Phase 1 and Phase 2 are complete.**

---

## Current Behavior (Phase 3 — Complete)

TeamBuilder uses JWT Bearer tokens as the only caller-identity mechanism.

1. **JWT Bearer token** — When a valid `Authorization: Bearer <token>` header
   is present, the authenticated `ClaimsPrincipal` is used. The claim named in
   `Jwt:PlayerIdClaim` (default: `sub`) carries the caller's player ID.
2. **No fallback** — When no authenticated JWT principal is present,
   `UserId` is `Guid.Empty`. The `X-User-Id` header is no longer read.

Write endpoints (`POST`, `PUT`, `DELETE`) on Teams, JoinRequests, Events, and
RosterImports require a valid JWT Bearer token. Unauthenticated write requests
return `401 Unauthorized`. Health and read (`GET`) endpoints remain anonymous.

---

## Affected Endpoints

The following controller actions read `ICurrentUserContext.UserId`:

| Controller | Action | Header use |
|---|---|---|
| `TeamsController` | `POST api/v1/teams` | Sets `OwnerId` on the new team. |
| `JoinRequestsController` | `POST api/v1/joinrequests` | Sets `PlayerId` on the join request. |
| `JoinRequestsController` | `PUT api/v1/joinrequests/{id}/process` | Identifies the processing user. |
| `EventsController` | `POST api/v1/events` | Sets `HostId` on the new event. |
| `RosterImportsController` | `POST api/v1/rosterimports` | Sets `ImportedByUserId` on the import record. |
| `RosterImportsController` | `PUT api/v1/rosterimports/{id}/process` | Identifies the processing user. |

Read-only (`GET`, `DELETE`) endpoints do not currently use caller identity but
will need authorization policies applied in a future phase.

---

## JWT Bearer Configuration Keys

All keys live under the `Jwt` section:

| Key | Purpose | Default |
|---|---|---|
| `Jwt:SigningKey` | Symmetric HMAC-SHA256 signing key (local dev / tests). When set, OIDC authority discovery is skipped. | _(empty — authority path used)_ |
| `Jwt:Issuer` | Expected token issuer when using the symmetric key path. | _(empty — issuer not validated)_ |
| `Jwt:Audience` | Expected token audience. | `teambuilder-api` |
| `Jwt:PlayerIdClaim` | JWT claim name that carries the TeamBuilder player ID. | `sub` |
| `Jwt:Authority` | OIDC authority URL for staging/production. Ignored when `Jwt:SigningKey` is set. | _(empty)_ |
| `Jwt:RequireHttpsMetadata` | Whether HTTPS is required for OIDC metadata. Only applies to the authority path. | `true` |

> **Never commit a real `Jwt:SigningKey` to source control.** Use
> `dotnet user-secrets` or environment variables for any value that must be
> kept out of `appsettings*.json`.

---

## Local Development Setup with `dotnet user-jwts`

`dotnet user-jwts` issues development tokens signed with a local symmetric key
and stores the key in `dotnet user-secrets` — it never touches
`appsettings.json`.

```powershell
# Issue a development token (run from the API project directory)
cd src/TeamBuilder.Api
dotnet user-jwts create --audience teambuilder-api --claim sub=<your-player-guid>
```

The command prints a Bearer token you can paste into Postman or an `.http`
file. It also writes the signing key to `dotnet user-secrets` under the path
`Authentication:Schemes:Bearer:SigningKeys:0:Value`.

> **Note:** `dotnet user-jwts` uses its own configuration path. To align it
> with TeamBuilder's `Jwt:SigningKey` and `Jwt:Issuer` keys you can copy the
> generated key into user-secrets manually:
>
> ```powershell
> dotnet user-secrets set "Jwt:SigningKey" "<key-from-user-jwts>"
> dotnet user-secrets set "Jwt:Issuer" "dotnet-user-jwts"
> ```

The claim expected for player identity is **`sub`** (configurable via
`Jwt:PlayerIdClaim`). The value must be a valid `Guid` string.

---

## Identity Resolution Summary

| Request carries | `ICurrentUserContext.UserId` value |
|---|---|
| Valid JWT with a `sub` GUID claim | GUID from the `sub` claim |
| Invalid / expired JWT on a **protected** endpoint | `401 Unauthorized` |
| No JWT on a **protected** endpoint | `401 Unauthorized` |
| Invalid / missing claim on an authenticated JWT | `Guid.Empty` |
| Anonymous request to a public endpoint | `Guid.Empty` |

---

## Implementation Phases

### Phase 1 — Authentication Configuration ✅

- JWT Bearer authentication registered in `Program.cs` (optional on all
  endpoints).
- `ClaimsCurrentUserContext` reads `sub` claim first; falls back to
  `X-User-Id` header when no authenticated principal is present.
- Symmetric key path (`Jwt:SigningKey`) for local development and tests.
- OIDC authority path (`Jwt:Authority`) prepared for staging/production.
- Integration tests cover the JWT claims path.
- `MapInboundClaims = false` ensures raw JWT claim names (`sub`) are preserved.

### Phase 2 — Authorization Enforcement (Write Endpoints) ✅

- `[Authorize]` added to all write actions on `TeamsController`,
  `JoinRequestsController`, `EventsController`, and `RosterImportsController`.
- Health endpoints (`/health`, `/health/ready`) explicitly marked
  `.AllowAnonymous()` in `Program.cs`.
- Read (`GET`) endpoints remain public.
- Integration tests updated: 401 coverage for unauthenticated write paths;
  JWT-authenticated write paths verified; health endpoints verified anonymous.

### Phase 3 — Ownership Enforcement and X-User-Id Removal ✅

- Ownership authorization added to all team, event, and roster import mutation endpoints:
  - `PUT /api/v1/teams/{id}`, `DELETE /api/v1/teams/{id}` — checks `OwnerId`.
  - `PUT /api/v1/events/{id}`, `DELETE /api/v1/events/{id}` — checks `HostId`.
  - `PUT /api/v1/rosterimports/{id}/process`, `DELETE /api/v1/rosterimports/{id}` — checks `ImportedByUserId`.
- Authenticated users who do not own the resource receive `403 Forbidden`.
- Unauthenticated requests continue to receive `401 Unauthorized`.
- Orphaned resources (where `HostId` or `ImportedByUserId` is `null`) return `409 Conflict`
  for any authenticated mutation attempt, making the failure explicit rather than silently
  denying all callers with `403`. An administrator path for orphan remediation is deferred.
- Create endpoints (`POST`) still set the owner/host/importer field from the `sub` claim.
- `X-User-Id` fallback removed from `ClaimsCurrentUserContext`. The authenticated JWT `sub`
  claim is now the only identity source. Anonymous requests resolve to `Guid.Empty`.
- Postman collection updated to use `Authorization: Bearer {{token}}` for all write requests.
- Integration tests updated: X-User-Id transition tests removed; missing-claim/Guid.Empty
  behavior added; existing 401/403/409 coverage preserved.

### Phase 4 — Identity Provider Selection

- Connect to Azure Entra ID, Auth0, or another OIDC provider for
  staging/production by setting `Jwt:Authority` (and removing `Jwt:SigningKey`
  from the environment).
- No code changes expected; only configuration.
- Add new tests for unauthenticated and unauthorized request paths (401, 403).

### Phase 5 — Update Postman Environment and Collection ✅

- Added `token` environment variable to
  `docs/postman/TeamBuilder.local.postman_environment.json`.
- All saved Postman write requests updated to use `Authorization: Bearer {{token}}`.
- `docs/postman-smoke-test.md` updated to document how to obtain a local dev
  token with `dotnet user-jwts` and set it in the environment.

---

## Open Decisions and Risks

| Topic | Decision needed | Risk if deferred |
|---|---|---|
| **Identity provider** | Which IdP (Entra ID, Auth0, local Identity)? | Phase 1 cannot start without this choice. |
| **Local dev token strategy** | Static dev token, `dotnet user-jwts`, or test IdP? | Blocks developer onboarding if not decided before Phase 1. |
| **User / player linking model** | Is `Player.Id` the same as the IdP subject claim, or is a separate link table needed? | Incorrect assumption here requires a data migration later. |
| **Admin / moderator roles** | What roles exist, and who can grant them? | Authorization policies (Phase 2) cannot be fully designed without this. |
| **Deployment secret management** | JWT signing keys / IdP client secrets in Octopus variables? | Secrets must not be committed; confirm Octopus variable naming before Phase 1. |
| **Token expiry and refresh** | Short-lived tokens with refresh, or long-lived dev tokens? | Affects Postman workflow (Phase 5) and frontend integration. |

---

## Related Files

| File | Relevance |
|---|---|
| `src/TeamBuilder.Application/Interfaces/ICurrentUserContext.cs` | Caller-identity abstraction. |
| `src/TeamBuilder.Api/Auth/ClaimsCurrentUserContext.cs` | JWT-only implementation — reads `sub` claim; returns `Guid.Empty` for unauthenticated requests. |
| `src/TeamBuilder.Api/Controllers/TeamsController.cs` | Uses `ICurrentUserContext.UserId` for team creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/JoinRequestsController.cs` | Uses `ICurrentUserContext.UserId` for join request creation and processing. |
| `src/TeamBuilder.Api/Controllers/EventsController.cs` | Uses `ICurrentUserContext.UserId` for event creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/RosterImportsController.cs` | Uses `ICurrentUserContext.UserId` for roster import creation and ownership checks. |
| `src/TeamBuilder.Api/Program.cs` | Authentication / authorization middleware registered here. |
| `docs/api.md` | API reference — Authentication section documents JWT Bearer as the supported identity mechanism. |
| `docs/postman-smoke-test.md` | Smoke test guide — includes token setup steps and Bearer token usage. |
| `docs/postman/TeamBuilder.postman_collection.json` | Collection — all write requests use `Authorization: Bearer {{token}}`. |
