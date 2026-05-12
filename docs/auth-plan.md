# TeamBuilder Authentication Implementation Plan

This document captures the current temporary user-context behavior, identifies
affected endpoints, and defines a phased plan for replacing the placeholder
mechanism with real authentication. **No authentication is implemented here.**

---

## Current Behavior (Temporary)

TeamBuilder uses an `X-User-Id` HTTP header as a lightweight caller-identity
placeholder. Endpoints that need to know who is making a request read this
header and treat the value as the acting player's ID.

Key characteristics of the current approach:

- No token validation, no signature verification, no session management.
- Any caller can supply any GUID and impersonate any player.
- If the header is omitted, `Guid.Empty` is used as the caller identity.
- This mechanism is suitable only for **local development and early API
  testing**. It must be replaced before any production exposure.

---

## Affected Endpoints

The following controller actions currently read `X-User-Id`:

| Controller | Action | Header use |
|---|---|---|
| `TeamsController` | `POST api/v1/teams` | Sets `OwnerId` on the new team. |
| `JoinRequestsController` | `POST api/v1/joinrequests` | Sets `PlayerId` on the join request. |
| `JoinRequestsController` | `PUT api/v1/joinrequests/{id}/process` | Identifies the processing user. |
| `EventsController` | `POST api/v1/events` | Sets `HostId` on the new event. |
| `RosterImportsController` | `POST api/v1/rosterimports` | Sets `ImportedByUserId` on the import record. |
| `RosterImportsController` | `PUT api/v1/rosterimports/{id}/process` | Identifies the processing user. |

Read-only (`GET`, `DELETE`) endpoints do not currently use `X-User-Id` but
will need authorization policies applied in a future phase.

---

## Proposed Future Authentication Approach

### Technology

- **ASP.NET Core authentication middleware** (`AddAuthentication` /
  `UseAuthentication` / `UseAuthorization`).
- **JWT bearer tokens** as the primary scheme, issued by an external identity
  provider or a lightweight local dev issuer.
- **Claims-based identity** — the authenticated `ClaimsPrincipal` replaces all
  direct header reads. The sub / `nameidentifier` claim carries the caller's
  identity.
- A thin **`IUserContext` service** abstracts claim extraction so controllers
  and services never read headers or claims directly.

### Identity Provider Options (decision pending)

| Option | Notes |
|---|---|
| Azure Active Directory / Entra ID | Recommended for production; integrates with Octopus secrets. |
| Auth0 | SaaS option; easy local dev flow. |
| ASP.NET Core Identity (local) | Useful if self-hosted user management is preferred. |
| Custom lightweight issuer | Development-only; not for production. |

The identity provider choice is a **deferred decision** that requires input
from the team before Phase 1 begins.

---

## Implementation Phases

### Phase 1 — Authentication Configuration

- Add the chosen identity provider SDK / NuGet package.
- Register `AddAuthentication().AddJwtBearer(...)` in `Program.cs`.
- Configure a local development token strategy (e.g., a development-only
  token issuer, or a `.http` file with a static dev token).
- Keep `X-User-Id` working in parallel during this phase to avoid breaking
  existing smoke tests.
- Do **not** enforce `[Authorize]` on any endpoints yet.

### Phase 2 — Authorization Policies

- Add `[Authorize]` to all write endpoints
  (`POST`, `PUT`, `DELETE`).
- Define role-based or policy-based authorization for sensitive actions
  (e.g., only team owners can process join requests for their team).
- Register policies in `Program.cs` via `AddAuthorization(options => ...)`.
- Read-only endpoints remain open or receive a lightweight `[AllowAnonymous]`
  annotation as a deliberate choice.

### Phase 3 — Replace X-User-Id with Authenticated User Context

- `ICurrentUserContext` already exists in `TeamBuilder.Application.Interfaces`
  with a `UserId` property. Extend it with `IsAuthenticated`:

  ```csharp
  public interface ICurrentUserContext
  {
      Guid UserId { get; }
      bool IsAuthenticated { get; }
  }
  ```

- Update `HeaderCurrentUserContext` to implement `IsAuthenticated = false`
  (the temporary header mechanism never represents a real authenticated identity).
- Implement `ClaimsUserContext` in `TeamBuilder.Api` backed by
  `IHttpContextAccessor` and `ClaimsPrincipal`.
- Register `ClaimsUserContext` in place of `HeaderCurrentUserContext` once JWT
  authentication is active.
- Controllers and services require **no changes** — they already depend only on
  `ICurrentUserContext` via dependency injection.

### Phase 4 — Update Integration Tests

- Add a test authentication handler (ASP.NET Core test auth scheme) to the
  integration test `WebApplicationFactory`.
- Replace any `X-User-Id` header injection in tests with the test auth scheme.
- Ensure all existing integration tests continue to pass (198 as of PR #52).
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
