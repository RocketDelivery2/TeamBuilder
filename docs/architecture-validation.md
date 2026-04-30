# TeamBuilder Architecture Validation Report

**Date:** 2026-04-30
**Branch:** `copilot/validate-teambuilder-architecture`
**Scope:** Validated against the intended enterprise API-first design.

---

## Overall Status

| Category | Result |
| --- | --- |
| Clean Architecture boundaries | ✅ Mostly clean — one structural concern |
| API-first / client-agnostic design | ✅ Pass |
| DTO separation | ✅ Pass |
| Pagination | ✅ Pass |
| EF Core Code First / Azure SQL readiness | ✅ Pass (migrations not committed) |
| Environment-based configuration | ✅ Pass |
| Secrets committed | ✅ None found |
| Controllers thin | ✅ Pass |
| Unit tests | ✅ Pass — meaningful coverage |
| Documentation | ✅ Pass |
| Authentication / Authorization | ❌ Not implemented — **high-priority gap** |
| Business logic placement | ⚠️ Logic in Infrastructure, not Application |
| Domain richness | ⚠️ Anemic domain model |

---

## Passed Checks

### 1. ASP.NET Core Web API is the middle layer ✅

`TeamBuilder.Api` is a dedicated ASP.NET Core Web API project. It sits
between any client layer and the SQL Server backend. It handles routing
(`/api/v1/`), serialization, Swagger, CORS, and health checks. No UI
framework or rendering concerns exist in this layer.

### 2. Frontend is client-agnostic ✅

The API returns JSON over HTTP/REST. CORS is configured to accept specific
origins per environment. There is no dependency on any frontend framework.
Swagger/OpenAPI documentation (`/swagger`) allows any client to discover
the contract without reading source code.

### 3. Layer boundaries are defined ✅ (with one concern — see Missing Items)

Four distinct projects exist with the correct dependency direction:

```text
TeamBuilder.Api → TeamBuilder.Application → TeamBuilder.Domain
TeamBuilder.Infrastructure → TeamBuilder.Application → TeamBuilder.Domain
```

- `TeamBuilder.Domain` has zero framework dependencies.
- `TeamBuilder.Application` holds interfaces and DTOs only.
- `TeamBuilder.Infrastructure` holds EF Core context, entity
  configurations, and service implementations.
- `TeamBuilder.Api` holds controllers and startup.

### 4. EF Core Code First prepared for Azure SQL ✅

- `UseSqlServer()` is configured in `Program.cs`.
- All entities inherit `BaseEntity` with `Id`, `CreatedAtUtc`,
  `UpdatedAtUtc`, and `RowVersion` (optimistic concurrency).
- Entity type configurations (`IEntityTypeConfiguration<T>`) are in
  `TeamBuilder.Infrastructure/Data/Configurations/` for all seven
  entities.
- Indexes are defined on commonly filtered fields (status, category, region,
  created timestamps).
- `appsettings.Production.json` uses the correct Azure SQL connection
  string format with `Encrypt=True;TrustServerCertificate=False`.
- UTC timestamps are enforced in `SaveChangesAsync` via `ChangeTracker`.

### 5. API uses DTOs and does not expose EF entities directly ✅

All controllers accept and return DTO types from
`TeamBuilder.Application.DTOs`. Service implementations call
`MapToDto()` before returning data. No EF entity is serialized directly to
the HTTP response.

### 6. Controllers are thin ✅

Controllers handle only:

- Input validation (`ModelState.IsValid`)
- Delegating to the service interface
- Mapping results to HTTP status codes (200, 201, 204, 404, 400)
- Logging with structured log messages and log sanitization

No business rules or data access logic exists in any controller.

### 7. List endpoints use pagination ✅

All five list endpoints (`GET /teams`, `/players`, `/events`,
`/joinrequests/teams/{id}`, `/joinrequests/players/{id}`,
`/rosterimports`) accept `page` and `pageSize` query parameters and
return `PaginatedResult<T>` which includes `TotalCount`, `TotalPages`,
`HasNextPage`, and `HasPreviousPage`.
Defaults: `page=1`, `pageSize=20`, `max=100`.

### 8. Configuration is environment-based ✅

Three environment-specific `appsettings` files are committed:

- `appsettings.Development.json`: LocalDB, wide CORS for local frontend
  ports, verbose logging.
- `appsettings.QA.json`: Octopus Deploy placeholder syntax for all
  secrets.
- `appsettings.Production.json`: Octopus placeholders, stricter EF
  logging, Application Insights key.

### 9. No secrets committed ✅

- Development uses Windows-integrated LocalDB (no password).
- QA and Production use `#{VariableName}` Octopus placeholder syntax for
  all credentials.
- `.gitignore` excludes `*.pfx`, `*.publishsettings`, `*.pubxml`,
  and SQL database files.
- `SECURITY.md` explicitly prohibits committing secrets and documents the
  private vulnerability reporting process.

### 10. Unit tests exist and are meaningful ✅

`TeamBuilder.Tests` provides meaningful coverage including:

- Domain entity initialization and property assignment
  (`Domain/TeamTests.cs`, `Domain/PlayerTests.cs`)
- CRUD operations for all five aggregates using EF InMemory database
- Join request create/approve/reject workflow
- Team membership leave with automatic "Recruiting" status refill
- Roster import processing
- Pagination behavior (`TotalPages`, `HasNextPage`, `HasPreviousPage`)
- Duplicate username prevention
- Edge cases (not-found returns, double-processing guard)

Tests use xUnit + FluentAssertions and are isolated via unique InMemory
database instances per test class.

### 11. README and deployment docs explain the architecture ✅

`README.md` includes:

- ASCII architecture diagram showing layer relationships
- API-first design rationale
- Full endpoint listing with pagination parameters
- Environment configuration table (Octopus variables)
- EF Code First migration commands
- Security considerations section
- Performance and scalability notes

`docs/deployment.md` includes:

- Step-by-step Azure SQL provisioning (CLI commands)
- Octopus Deploy variable definitions
- Deployment process steps (deploy package → configure IIS → apply
  migrations → health check)
- Rollback strategy
- Pre- and post-deployment checklists

---

## Missing Items

### M1 — No authentication or authorization middleware ❌

#### Severity: Critical

The API has no authentication. `app.UseAuthentication()` is absent from
`Program.cs`. The `X-User-Id` custom header is accepted as-is from any
caller with no verification, meaning any consumer can impersonate any user.

`app.UseAuthorization()` is registered but has no effect without
authentication configured. No controller or action has `[Authorize]`
attributes.

**Impact:** All mutation endpoints (create/update/delete for teams, events,
players, join requests) are publicly accessible without identity
verification.

**Recommendation:** Implement JWT bearer authentication (or Azure AD /
Entra ID) before any production deployment. Add `[Authorize]` to all
controllers. Replace the `X-User-Id` header with claims extracted from the
validated token.

### M2 — Business logic in Infrastructure layer, not Application layer ⚠️

#### Severity: Medium (Infrastructure placement)

All service implementations (`TeamService`, `JoinRequestService`, etc.)
live in `TeamBuilder.Infrastructure.Services`, directly referencing
`TeamBuilderDbContext`. This places orchestration logic in the
Infrastructure layer rather than the Application layer.

Examples of business logic currently in Infrastructure:

- Changing team status from `Full` to `Recruiting` when a member leaves
  (`TeamService.RemoveMemberAsync`)
- Preventing duplicate join requests (`JoinRequestService.CreateAsync`)
- Incrementing/decrementing `CurrentMemberCount` on join approve/leave

**Impact:** The Application layer is a shell of interfaces and DTOs.
Swapping EF Core for another data access strategy (e.g., Dapper, a different
ORM, or a microservice boundary) would require touching business rules.

**Recommendation:** Move service implementations to
`TeamBuilder.Application`, introduce repository interfaces (e.g.,
`ITeamRepository`) in `TeamBuilder.Application`, and limit
`TeamBuilder.Infrastructure` to the repository implementations and the
`DbContext`.

### M3 — Anemic domain model ⚠️

#### Severity: Low (by design choice, but worth noting)

Domain entities (`Team`, `Player`, `TeamEvent`, etc.) are plain data
containers with no behavior methods. Business rules such as "a full team
becomes Recruiting when a member leaves" are expressed as imperative logic
in service methods rather than as entity methods.

**Recommendation:** Consider adding domain methods to entities (e.g.,
`Team.AddMember()`, `Team.RemoveMember()`) that encapsulate the
state-change rules, or document the decision to use an anemic model
explicitly.

### M4 — No EF Core migrations committed ⚠️

#### Severity: Medium (Missing migrations)

The `TeamBuilder.Infrastructure/Data/` directory contains entity
configurations but no `Migrations/` folder. A fresh
`dotnet ef database update` will fail until an initial migration is
created.

**Recommendation:** Run the following command and commit the generated
files:

```bash
dotnet ef migrations add InitialCreate \
  --startup-project src/TeamBuilder.Api \
  --project src/TeamBuilder.Infrastructure
```

### M5 — `GetAll` ProducesResponseType uses `typeof(object)` ⚠️

#### Severity: Low (API documentation)

All list endpoints declare
`[ProducesResponseType(typeof(object), StatusCodes.Status200OK)]` instead
of `[ProducesResponseType(typeof(PaginatedResult<TeamDto>), StatusCodes.Status200OK)]`.
This prevents Swagger from generating accurate response schemas for list
endpoints.

**Recommendation:** Replace `typeof(object)` with the concrete
`PaginatedResult<T>` type on all list action attributes.

### M6 — Application Insights SDK not wired in ⚠️

#### Severity: Low (Telemetry configuration)

`appsettings.Production.json` contains an
`ApplicationInsights.ConnectionString` placeholder, and `deployment.md`
documents the package to add — but
`Microsoft.ApplicationInsights.AspNetCore` is not referenced in
`TeamBuilder.Api.csproj` and `AddApplicationInsightsTelemetry()` is not
called in `Program.cs`.

**Recommendation:** Add the package and telemetry registration, or remove
the config key until it is implemented.

---

## Security Risks

### S1 — Unauthenticated API (Critical)

As noted in M1, all endpoints are publicly accessible. This is the
highest-priority risk before any production use.

### S2 — Wildcard CORS in base `appsettings.json`

`appsettings.json` sets `"AllowedOrigins": "*"`. While
`appsettings.Development.json` overrides this with specific localhost
origins, and Production/QA use Octopus placeholders, the base file acts as a
fallback for any unrecognized environment name. If the
`ASPNETCORE_ENVIRONMENT` variable is misconfigured, wildcard CORS is in
effect.

**Recommendation:** Change the base `appsettings.json` default to `""`
(empty/deny) so that a misconfigured environment fails closed rather than
open.

### S3 — Internal exception messages returned to callers

Controllers return `BadRequest(new { error = ex.Message })` for general
exceptions. This can expose internal implementation details (e.g., SQL error
messages, stack traces embedded in `ex.Message`, entity names) to API
consumers in non-development environments.

**Recommendation:** Add a global exception-handling middleware (e.g.,
`UseExceptionHandler`) that returns a generic problem-detail response and
logs the full exception server-side. Reserve exception details for
`Development` only. Consider using `ProblemDetails` via
`AddProblemDetails()`.

### S4 — `db_owner` SQL role in deployment docs

`docs/deployment.md` shows granting `db_owner` to the API's SQL user.
This violates the principle of least privilege — the API only needs
read/write on its own tables, not the ability to alter schema.

**Recommendation:** Grant only `db_datareader` and `db_datawriter` (or a
custom role) for runtime access. Use a separate privileged credential only
during migration steps.

### S5 — No rate limiting

There is no rate-limiting middleware. Endpoints like
`POST /api/v1/players` (creates accounts) and
`POST /api/v1/joinrequests` (join flows) are vulnerable to abuse at scale.

**Recommendation:** Add `builder.Services.AddRateLimiter()` (available in
.NET 7+) or use Azure API Management throttling policies.

---

## Scalability Concerns

### SC1 — Denormalized `CurrentMemberCount` counter

`Team.CurrentMemberCount` is maintained manually in service code
(increment on join approve, decrement on leave). Under concurrent request
load this counter can diverge from the actual `TeamMembers` count. The
`RowVersion` optimistic concurrency check helps but does not eliminate the
race.

**Recommendation:** Either use a `COUNT` subquery when querying team
members or implement a distributed locking mechanism for membership changes.

### SC2 — Tags stored as a delimited string

`Team.Tags` and similar fields are stored as comma-separated strings
(e.g., `"fps,competitive"`) rather than in a normalized `TeamTags`
junction table. This prevents efficient tag-based filtering at the database
level.

**Recommendation:** Introduce a normalized tags table for entities that
require tag-based querying or filtering at scale.

### SC3 — No output caching or distributed cache

Read-heavy endpoints (team listings, event listings) hit the database on
every request. There is no response caching, output caching, or distributed
cache (e.g., Redis) in the pipeline.

**Recommendation:** Add `UseOutputCache()` for read-only list endpoints or
configure a Redis distributed cache for frequently accessed lookups.

### SC4 — No background job processing for roster imports

`RosterImportService.ProcessAsync` runs synchronously in the HTTP request
thread. For large roster files this blocks the request for an extended time.

**Recommendation:** Process roster imports asynchronously via a background
queue (e.g., Azure Service Bus + Azure Functions, or Hangfire).

### SC5 — EF InMemory used in tests (not SQL Server)

Tests use `UseInMemoryDatabase()` which does not enforce foreign-key
constraints, indexes, or SQL Server–specific behaviors. Some bugs
(constraint violations, missing indexes on filter queries) will only surface
against a real SQL Server instance.

**Recommendation:** Add a test category that runs against a SQL Server
LocalDB or Testcontainers instance for integration-level coverage.

---

## Recommended Next PRs

- **Priority 🔴 1**: Add JWT / Entra ID authentication
  - Register `AddAuthentication().AddJwtBearer()`, add `[Authorize]` to
    all controllers, replace `X-User-Id` with token claims.

- **Priority 🔴 2**: Commit initial EF Core migration
  - Run `dotnet ef migrations add InitialCreate` and commit the
    `Migrations/` folder so the database can be provisioned from source.

- **Priority 🟠 3**: Add global exception handler middleware
  - Replace per-controller `catch (Exception)` blocks with a centralized
    `ProblemDetails` response, keeping internal error details server-side
    only.

- **Priority 🟠 4**: Move service implementations to Application layer
  - Introduce `ITeamRepository` / `IPlayerRepository` interfaces in
    Application, move service logic to Application, and limit Infrastructure
    to EF repository implementations.

- **Priority 🟠 5**: Fix CORS default to deny-all
  - Set `"AllowedOrigins": ""` in base `appsettings.json` so
    misconfigured environments fail closed.

- **Priority 🟡 6**: Fix `ProducesResponseType` on list endpoints
  - Replace `typeof(object)` with `typeof(PaginatedResult<TeamDto>)`
    etc. for accurate Swagger schemas.

- **Priority 🟡 7**: Add rate limiting middleware
  - Apply `AddRateLimiter()` with per-endpoint policies for write
    operations.

- **Priority 🟡 8**: Wire Application Insights or remove placeholder
  - Either add `Microsoft.ApplicationInsights.AspNetCore` and
    `AddApplicationInsightsTelemetry()`, or remove the unreferenced config
    key.

- **Priority 🟡 9**: Restrict SQL user role
  - Update deployment docs to grant `db_datareader`/`db_datawriter` at
    runtime; reserve elevated credentials for migration steps only.

- **Priority 🟢 10**: Add SQL Server integration tests
  - Introduce a Testcontainers-based test project to catch SQL-specific
    constraint and query issues not caught by InMemory.
