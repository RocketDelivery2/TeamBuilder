# TeamBuilder Authentication Implementation Plan

This document captures the current caller-identity behavior and phased auth
rollout for JWT bearer auth.

---

## Current Behavior (Phase 2 — Authorization Enforcement)

TeamBuilder requires JWT bearer auth on write endpoints.

1. **JWT bearer auth** — When a valid `Authorization: Bearer <token>` header
   is present, the authenticated `ClaimsPrincipal` is used. The claim named in
   `Jwt:PlayerIdClaim` (default: `sub`) carries the caller's player ID.
2. **No anonymous write access** — Write endpoints return `401 Unauthorized`
   when no JWT is present.
3. **Public read and health endpoints** — Read and health endpoints remain
   public. Anonymous requests resolve to `Guid.Empty`.

The write endpoints below use the resolved caller identity for ownership checks
and resource ownership fields.

---

## Affected Endpoints

The following controller actions read `ICurrentUserContext.UserId`:

| Controller | Action | Header use |
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
| Invalid / missing claim on an authenticated JWT | `Guid.Empty` |
| Anonymous request to a public endpoint | `Guid.Empty` |

---

## Implementation Phases

### Phase 1 — Authentication Configuration ✅

- JWT bearer authentication is registered in `Program.cs`.
- `ClaimsCurrentUserContext` reads the JWT `sub` claim first and falls back to `X-User-Id`.
- `MapInboundClaims = false` keeps raw JWT claim names such as `sub`.
- Integration tests cover both JWT and header-based identity resolution.

### Phase 2 — Authorization Enforcement ✅

- `[Authorize]` is applied to all write actions on `TeamsController`,
  `JoinRequestsController`, `EventsController`, and `RosterImportsController`.
- Write endpoints without a JWT now return `401 Unauthorized`.
- Integration tests cover 401 responses for unauthenticated write requests.

### Phase 3 — Ownership Enforcement and Header Removal

- Ownership authorization remains on team, event, and roster import mutation endpoints.
- Remove the `X-User-Id` fallback once all clients are sending JWT bearer tokens.
- Add final tests for forbidden and conflict request paths.

### Phase 4 — Identity Provider Selection

- Connect to Azure Entra ID, Auth0, or another OIDC provider for
  staging/production by setting `Jwt:Authority` (and removing `Jwt:SigningKey`
  from the environment).
- No code changes expected; only configuration.
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
| **Identity provider** | Which IdP (Entra ID, Auth0, local Identity)? | Needed for the phase-4 rollout. |
| **Local dev token strategy** | Static dev token, `dotnet user-jwts`, or test IdP? | Use `dotnet user-jwts` plus the Development config defaults until a staging IdP is chosen. |
| **User / player linking model** | Is `Player.Id` the same as the IdP subject claim, or is a separate link table needed? | Incorrect assumption here requires a data migration later. |
| **Admin / moderator roles** | What roles exist, and who can grant them? | Authorization policies (Phase 2) cannot be fully designed without this. |
| **Deployment secret management** | JWT signing keys / IdP client secrets in Octopus variables? | Secrets must not be committed; confirm Octopus variable naming before Phase 1. |
| **Token expiry and refresh** | Short-lived tokens with refresh, or long-lived dev tokens? | Affects Postman workflow (Phase 5) and frontend integration. |

---

## Related Files

| File | Relevance |
|---|---|
| `src/TeamBuilder.Application/Interfaces/ICurrentUserContext.cs` | Caller-identity abstraction. |
| `src/TeamBuilder.Api/Auth/ClaimsCurrentUserContext.cs` | Reads the JWT `sub` claim first and falls back to `X-User-Id`; returns `Guid.Empty` when neither is present. |
| `src/TeamBuilder.Api/Controllers/TeamsController.cs` | Uses `ICurrentUserContext.UserId` for team creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/JoinRequestsController.cs` | Uses `ICurrentUserContext.UserId` for join request creation and processing. |
| `src/TeamBuilder.Api/Controllers/EventsController.cs` | Uses `ICurrentUserContext.UserId` for event creation and ownership checks. |
| `src/TeamBuilder.Api/Controllers/RosterImportsController.cs` | Uses `ICurrentUserContext.UserId` for roster import creation and ownership checks. |
| `src/TeamBuilder.Api/Program.cs` | Authentication / authorization middleware registered here. |
| `docs/api.md` | API reference — Authentication section documents JWT bearer auth plus the `X-User-Id` fallback. |
| `docs/postman-smoke-test.md` | Smoke test guide — includes token setup steps and bearer token usage. |
| `docs/postman/TeamBuilder.postman_collection.json` | Collection — all write requests use `Authorization: Bearer <token>`. |
