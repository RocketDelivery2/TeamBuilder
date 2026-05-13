# TeamBuilder Authentication Implementation Plan

This document captures the current temporary user-context behavior, identifies
affected endpoints, and defines a phased plan for replacing the placeholder
mechanism with real authentication. **Phase 1 is complete. Phase 2 is in
progress.**

---

## Current Behavior (Phase 1 — Transition)

TeamBuilder supports two caller-identity mechanisms in parallel:

1. **JWT Bearer token** — When a valid `Authorization: Bearer <token>` header
   is present, the authenticated `ClaimsPrincipal` is used. The claim named in
   `Jwt:PlayerIdClaim` (default: `sub`) carries the caller's player ID.
2. **`X-User-Id` header fallback** — When no authenticated JWT principal is
   present, the `X-User-Id` header is read as before. This preserves the
   existing development workflow without breaking any Postman smoke tests.

The `ClaimsCurrentUserContext` service implements both paths. If a request has
neither a valid JWT nor an `X-User-Id` header, `UserId` is `Guid.Empty` as it
was before.

No `[Authorize]` attribute has been added to read-only endpoints. Write
endpoints (`POST`, `PUT`, `DELETE`) on Teams, JoinRequests, Events, and
RosterImports now require a valid JWT Bearer token. Unauthenticated write
requests return `401 Unauthorized`. Health and read (`GET`) endpoints remain
anonymous.

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

## Transition Behavior Summary

| Request carries | `ICurrentUserContext.UserId` value |
|---|---|
| Valid JWT with a `sub` GUID claim | GUID from the `sub` claim |
| JWT present but invalid / expired | `Guid.Empty` (falls through to header) |
| No JWT, valid `X-User-Id` header | GUID from the header |
| No JWT, missing / invalid `X-User-Id` | `Guid.Empty` |

---

## Implementation Phases

### Phase 1 — Authentication Configuration ✅

- JWT Bearer authentication registered in `Program.cs` (optional on all
  endpoints).
- `ClaimsCurrentUserContext` reads `sub` claim first; falls back to
  `X-User-Id` header when no authenticated principal is present.
- Symmetric key path (`Jwt:SigningKey`) for local development and tests.
- OIDC authority path (`Jwt:Authority`) prepared for staging/production.
- Integration tests cover both the JWT claims path and the `X-User-Id` fallback.
- `MapInboundClaims = false` ensures raw JWT claim names (`sub`) are preserved.

### Phase 2 — Authorization Enforcement (Write Endpoints) ✅

- `[Authorize]` added to all write actions on `TeamsController`,
  `JoinRequestsController`, `EventsController`, and `RosterImportsController`.
- Health endpoints (`/health`, `/health/ready`) explicitly marked
  `.AllowAnonymous()` in `Program.cs`.
- Read (`GET`) endpoints remain public.
- `X-User-Id` fallback is **still wired** in `ClaimsCurrentUserContext` but is
  unreachable on protected endpoints without a valid JWT. It remains available
  for unauthenticated contexts (e.g., middleware or future public read endpoints
  that may need caller identity).
- Integration tests updated: 401 coverage for unauthenticated write paths;
  JWT-authenticated write paths verified; health endpoints verified anonymous.

### Phase 3 — Replace X-User-Id Completely

- Once JWT is enforced, remove the `X-User-Id` fallback path from
  `ClaimsCurrentUserContext`.
- Add `IsAuthenticated` to `ICurrentUserContext` if needed by business logic.
- Remove `HeaderCurrentUserContext` from the codebase.

### Phase 4 — Identity Provider Selection

- Connect to Azure Entra ID, Auth0, or another OIDC provider for
  staging/production by setting `Jwt:Authority` (and removing `Jwt:SigningKey`
  from the environment).
- No code changes expected; only configuration.
- Add new tests for unauthenticated and unauthorized request paths (401, 403).

### Phase 5 — Update Postman Environment and Collection

- Add a `token` environment variable to
  `docs/postman/TeamBuilder.local.postman_environment.json`.
- Add a pre-request script or Auth tab configuration to the collection to
  attach `Authorization: Bearer {{token}}`.
- Update `docs/postman-smoke-test.md` to document how to obtain a local dev
  token and set it in the environment.
- Remove or archive the `X-User-Id` header from all saved Postman requests.

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
| `src/TeamBuilder.Application/Interfaces/ICurrentUserContext.cs` | Caller-identity abstraction — already in place; extend with `IsAuthenticated` in Phase 3. |
| `src/TeamBuilder.Api/Auth/HeaderCurrentUserContext.cs` | Temporary header-based implementation — replaced by `ClaimsUserContext` in Phase 3. |
| `src/TeamBuilder.Api/Controllers/TeamsController.cs` | Uses `ICurrentUserContext.UserId` for team creation. |
| `src/TeamBuilder.Api/Controllers/JoinRequestsController.cs` | Uses `ICurrentUserContext.UserId` for join request creation and processing. |
| `src/TeamBuilder.Api/Controllers/EventsController.cs` | Uses `ICurrentUserContext.UserId` for event creation. |
| `src/TeamBuilder.Api/Controllers/RosterImportsController.cs` | Uses `ICurrentUserContext.UserId` for roster import creation and processing. |
| `src/TeamBuilder.Api/Program.cs` | Authentication / authorization middleware will be registered here. |
| `docs/api.md` | API reference — Authentication section documents `X-User-Id` temporary behavior. |
| `docs/postman-smoke-test.md` | Smoke test guide — will need token setup steps added in Phase 5. |
| `docs/postman/TeamBuilder.postman_collection.json` | Collection — will need Bearer token auth added in Phase 5. |
