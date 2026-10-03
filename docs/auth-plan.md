# TeamBuilder Authentication Implementation Plan

This document records the current JWT caller-identity behavior and the
remaining identity-provider rollout work.

---

## Current Behavior

TeamBuilder validates JWT bearer tokens and applies authorization on selected
routes. This is not a global policy for every write endpoint: player
create/update/delete currently do not require a JWT.

1. **JWT bearer auth** — When a valid `Authorization: Bearer <token>` header
   is present, the authenticated `ClaimsPrincipal` is used. The claim named in
   `Jwt:PlayerIdClaim` (default: `sub`) carries the caller's player ID.
2. **Protected writes require authentication** — Write endpoints marked with
   `[Authorize]` return `401 Unauthorized` when no JWT is present. Player
   create/update/delete routes currently do not require a JWT.
3. **Public routes** — Read and health endpoints remain public. Anonymous
   requests resolve to `Guid.Empty`.

The protected endpoints below use the resolved caller identity for ownership
checks and resource ownership fields. The player write endpoints are not
currently protected and do not use this identity.

---

## Affected Endpoints

The following controller actions read `ICurrentUserContext.UserId`:

| Controller | Action | Current caller identity behavior |
|---|---|---|
| `TeamsController` | `POST api/v1/teams` | Sets `OwnerId` on the new team. |
| `TeamsController` | `PUT api/v1/teams/{id}` | Uses the resolved caller identity for ownership checks. |
| `TeamsController` | `DELETE api/v1/teams/{id}` | Uses the resolved caller identity for ownership checks. |
| `TeamsController` | `POST api/v1/teams/{teamId}/members/{playerId}/leave` | Uses the resolved caller identity. |
| `JoinRequestsController` | `POST api/v1/joinrequests` | Sets `PlayerId` on the join request. |
| `JoinRequestsController` | `PUT api/v1/joinrequests/{id}/process` | Identifies the processing user. |
| `EventsController` | `POST api/v1/events` | Sets `HostId` on the new event. |
| `EventsController` | `PUT api/v1/events/{id}` | Uses the resolved caller identity for ownership checks. |
| `EventsController` | `DELETE api/v1/events/{id}` | Uses the resolved caller identity for ownership checks. |
| `RosterImportsController` | `POST api/v1/rosterimports` | Sets `ImportedByUserId` on the import record. |
| `RosterImportsController` | `PUT api/v1/rosterimports/{id}/process` | Uses the resolved caller identity for ownership checks. |
| `RosterImportsController` | `DELETE api/v1/rosterimports/{id}` | Uses the resolved caller identity for ownership checks. |

Team update/delete are restricted to the team owner, join-request processing
is restricted to the team owner, and the voluntary team-leave route requires
the caller's ID to match `{playerId}`. Event changes are host-restricted and
roster-import processing/deletion are importer-restricted.

---

## JWT Keys

All keys live under the `Jwt` section:

| Key | Purpose | Default |
|---|---|---|
| `Jwt:SigningKey` | Symmetric HMAC-SHA256 signing key (local dev / tests). When set, OIDC authority discovery is skipped. | _(empty — authority path used)_ |
| `Jwt:Issuer` | Expected token issuer when using the symmetric key path. | `dotnet-user-jwts` in Development |
| `Jwt:Audience` | Expected token audience. | `teambuilder-api` |
| `Jwt:PlayerIdClaim` | JWT claim name that carries the TeamBuilder player ID. | `sub` |
| `Jwt:Authority` | OIDC authority URL for staging/production. Ignored when `Jwt:SigningKey` is set. | _(empty)_ |
| `Jwt:RequireHttpsMetadata` | Whether HTTPS is required for OIDC metadata. Only applies to the authority path. | `true` |
| `Jwt:ExternalIdentity:SubjectClaim` | Claim carrying the caller's issuer-scoped external subject, used by `/api/v1/players/me`. Any string; not required to be a GUID. Use `oid` for Microsoft Entra. | `sub` |
| `Jwt:ExternalIdentity:TenantIdClaim` | Optional claim stored as `PlayerIdentity.TenantId` metadata when present. | `tid` |
| `Jwt:ExternalIdentity:Provider` | Provider name stored as `PlayerIdentity.Provider` metadata (e.g. `entra`). | `oidc` |

> **Never commit a real `Jwt:SigningKey` to source control.** Use
> `dotnet user-secrets` or environment variables for any value that must be
> kept out of `appsettings*.json`.

---

## Player Onboarding (`/api/v1/players/me`)

`GET` and `POST /api/v1/players/me` resolve the caller by external identity
instead of by player GUID. They use a separate JWT bearer scheme,
`ExternalIdentity`, which validates tokens with the same `Jwt` settings as the
default scheme but does not require `Jwt:PlayerIdClaim` to be a GUID. It
requires the token's `iss` claim and the `Jwt:ExternalIdentity:SubjectClaim`
claim instead. Issuer + subject is looked up in `PlayerIdentities`; the
subject is never used as `Player.Id`.

All other protected endpoints still use the default scheme and
`Jwt:PlayerIdClaim` unchanged.

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

The command prints a token you can paste into Postman or an `.http` file. It
also writes the signing key to `dotnet user-secrets` under the path
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
| Invalid / expired JWT on a protected endpoint | `401 Unauthorized` |
| No JWT on a protected endpoint | `401 Unauthorized` |
| Missing or non-GUID configured player claim | `401 Unauthorized` during token validation |
| Anonymous request to a public endpoint | `Guid.Empty` |

---

## Implementation Phases

### Phase 1 — Authentication Configuration ✅

- JWT bearer authentication is registered in `Program.cs`.
- `ClaimsCurrentUserContext` reads the configured player-ID claim (default `sub`) from the authenticated principal; there is no `X-User-Id` fallback.
- `MapInboundClaims = false` keeps raw JWT claim names such as `sub`.
- Token validation rejects a missing or non-GUID configured player-ID claim.
- Integration tests cover JWT identity resolution.

### Phase 2 — Authorization Enforcement ✅

- `[Authorize]` is applied to all write actions on `TeamsController`,
  `JoinRequestsController`, `EventsController`, and `RosterImportsController`.
- Protected write actions without a JWT return `401 Unauthorized`. Player
  create/update/delete actions are not currently protected.
- Integration tests cover 401 responses for unauthenticated write requests.

### Phase 3 — Resource Authorization

- Team update/delete are restricted to the owner; join-request processing is
  restricted to the team owner; event mutations are restricted to the host;
  roster-import processing/deletion are restricted to the importer.
- The voluntary team-leave route is self-only: the authenticated player ID
  must equal `{playerId}`. It is not an owner-kick route.
- Player create/update/delete authorization remains unimplemented.

### Phase 4 — Identity Provider Selection

- The API supports symmetric-key and OIDC-authority JWT validation. Select and
  verify the identity provider and its QA/production configuration with the
  environment operators; repository contents do not establish deployed
  provider state.
- For OIDC, configure `Jwt:Authority` and do not set `Jwt:SigningKey` in that
  environment. Confirm the configured player-ID claim matches issued tokens.
- Full rollout guide, provider-specific setup steps, environment variable
  reference, and a smoke-test checklist are documented in
  [docs/oidc-rollout.md](oidc-rollout.md).
- Add new integration tests for unauthenticated and unauthorized request paths
  (401, 403) once a staging IdP is confirmed.

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
| **Identity provider** | Which IdP is selected and configured in each environment? | Verify provider-side state and token claims before rollout. |
| **Local dev token strategy** | Static dev token, `dotnet user-jwts`, or test IdP? | Use `dotnet user-jwts` plus the Development config defaults until a staging IdP is chosen. |
| **User / player linking model** | Is `Player.Id` the same as the IdP subject claim, or is a separate link table needed? | Incorrect assumption here requires a data migration later. |
| **Administrative authorization roles** | Are admin/moderator authorization roles needed, and who can grant them? These are distinct from the `TeamRole` membership values. | No administrative member-removal capability is currently implemented. |
| **Deployment secret management** | How are JWT signing keys / IdP credentials managed in each deployed environment? | Secrets must not be committed; verify provider-side configuration before rollout. |
| **Token expiry and refresh** | Short-lived tokens with refresh, or long-lived dev tokens? | Affects Postman workflow (Phase 5) and frontend integration. |

---

## Related Files

| File | Relevance |
|---|---|
| `src/TeamBuilder.Application/Interfaces/ICurrentUserContext.cs` | Caller-identity abstraction. |
| `src/TeamBuilder.Api/Auth/ClaimsCurrentUserContext.cs` | Reads the configured player-ID claim from an authenticated principal. JWT validation rejects a missing or non-GUID claim on protected routes; anonymous public requests resolve to `Guid.Empty`. |
| `src/TeamBuilder.Api/Controllers/TeamsController.cs` | Uses `ICurrentUserContext.UserId` for team creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/JoinRequestsController.cs` | Uses `ICurrentUserContext.UserId` for join request creation and processing. |
| `src/TeamBuilder.Api/Controllers/EventsController.cs` | Uses `ICurrentUserContext.UserId` for event creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/RosterImportsController.cs` | Uses `ICurrentUserContext.UserId` for roster import creation and ownership checks. |
| `src/TeamBuilder.Api/Program.cs` | Authentication / authorization middleware registered here. |
| `docs/api.md` | API reference — documents JWT-protected routes and route-specific authorization. |
| `docs/postman-smoke-test.md` | Smoke test guide — includes token setup steps and bearer token usage. |
| `docs/postman/TeamBuilder.postman_collection.json` | Collection — all write requests use `Authorization: Bearer <token>`. |
