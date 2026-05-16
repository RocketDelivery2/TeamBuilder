# TeamBuilder — Deployment Next Steps

This document describes the recommended future hosting, database, and deployment
strategy for TeamBuilder. Nothing in this document has been implemented yet.
Each section identifies what needs to be done, why, and suggested sequencing.

---

## Target Architecture

```text
GitHub (source control + CI/CD trigger)
  └── GitHub Actions (CI: build, test, CodeQL, Dependabot)
        └── Octopus Deploy (CD: package, promote, deploy)
              ├── Development (Azure App Service)
              ├── QA         (Azure App Service)
              └── Production (Azure App Service)
                    └── Azure SQL Database
                    └── Application Insights
```

---

## 1. Azure Hosting — Azure App Service

### Recommended setup

| Setting | Value |
|---|---|
| **Service** | Azure App Service (Windows or Linux) |
| **App** | `TeamBuilder.Api` |
| **Runtime** | .NET 10 |
| **Plan** | B2 or higher for QA/Production; F1 free tier for Development only |
| **HTTPS** | Enforce HTTPS only in App Service settings |
| **Deployment slots** | Use staging slot for zero-downtime swaps in Production |

### App Settings (Azure Portal or Octopus variables)

```text
ASPNETCORE_ENVIRONMENT        = Production
ConnectionStrings__TeamBuilderSql = <managed by Octopus or Key Vault>
AllowedOrigins                = https://teambuilder.info
APPLICATIONINSIGHTS_CONNECTION_STRING = <managed by Octopus or Key Vault>
```

### Render QA Docker hosting

If TeamBuilder API is deployed to Render QA as a Docker Web Service, use:

```text
Service name                  = teambuilder-api-qa
Render URL                    = https://teambuilder-api-qa.onrender.com
Runtime                       = Docker
Environment                   = QA
ASPNETCORE_ENVIRONMENT        = QA
ASPNETCORE_URLS               = http://0.0.0.0:${PORT}
AllowedOrigins                = https://teambuilder.info,https://teambuilder-api-qa.onrender.com
Jwt__Authority                = https://login.microsoftonline.com/299120a7-9680-48a3-b1ad-150125d656ce/v2.0
Jwt__Audience                 = api://5457c4d7-0746-4337-ab67-c5c1061b2963
Jwt__Issuer                   = https://login.microsoftonline.com/299120a7-9680-48a3-b1ad-150125d656ce/v2.0
Jwt__PlayerIdClaim            = sub
Jwt__RequireHttpsMetadata     = true
Jwt__SigningKey               = not set
ConnectionStrings__DefaultConnection = not set yet
```

Keep `Jwt__SigningKey` unset for Entra/OIDC JWT validation, and leave the database connection unset until the real database is ready.


Do **not** store these values in source control. Use Octopus variables, Azure
App Settings, or Azure Key Vault references.

### Managed identity

Configure a system-assigned managed identity on the App Service and grant it
access to Azure SQL using Azure AD authentication where possible. This removes
the need for username/password connection strings in most scenarios.

---

## 2. Azure SQL Database

### Azure SQL recommended setup

| Setting | Value |
|---|---|
| **Service** | Azure SQL Database (single database) |
| **DTU / vCore** | Basic / S0 for Development; S2+ for QA/Production |
| **Backup** | Enable geo-redundant backup for Production |
| **Firewall** | Allow only App Service outbound IPs; deny public access |
| **Authentication** | Azure AD authentication with managed identity preferred |

### Connection string pattern

```text
Server=tcp:<server>.database.windows.net,1433;
Initial Catalog=TeamBuilder;
Authentication=Active Directory Managed Identity;
```

For local development (SQL Server / LocalDB):

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=(localdb)\\mssqllocaldb;Database=TeamBuilder;Trusted_Connection=True;"
  }
}
```

---

## 3. EF Core Code First Migrations

TeamBuilder uses EF Core Code First. Migrations have not been created yet.
The following workflow is recommended.

### One-time setup

```bash
dotnet tool install --global dotnet-ef
```

### Creating a migration locally

```bash
dotnet ef migrations add InitialCreate \
  --project src/TeamBuilder.Infrastructure \
  --startup-project src/TeamBuilder.Api
```

Review the generated migration before committing it.

### Applying migrations locally

```bash
dotnet ef database update \
  --project src/TeamBuilder.Infrastructure \
  --startup-project src/TeamBuilder.Api
```

### Generating a SQL script for controlled deployment

```bash
dotnet ef migrations script \
  --idempotent \
  --project src/TeamBuilder.Infrastructure \
  --startup-project src/TeamBuilder.Api \
  --output migrations.sql
```

Apply `migrations.sql` through an Octopus `Run a Script` step with approval
gates before Production. Never apply migrations automatically without review.

### Rules

- Never apply migrations directly to Production without a QA gate.
- Always generate an idempotent script for non-Development deployments.
- Keep migrations in source control under
  `src/TeamBuilder.Infrastructure/Migrations/`.
- Do **not** use `EnsureCreated()` in Production; use migrations only.

---

## 4. Octopus Deploy

### Package

Build and publish the API artifact in GitHub Actions (or a separate build step):

```bash
dotnet publish src/TeamBuilder.Api/TeamBuilder.Api.csproj \
  --configuration Release \
  --output publish/TeamBuilder.Api
```

Package using the Octopus CLI or a GitHub Actions step:

```bash
octo pack --id=TeamBuilder.Api \
          --version=<version> \
          --basePath=publish/TeamBuilder.Api \
          --outFolder=artifacts
```

Push the artifact to the Octopus built-in feed or a NuGet feed.

### Deployment process

Recommended Octopus Deploy steps per environment:

1. **Run SQL migration script** — `Run a Script` step running `migrations.sql`
   against the environment's Azure SQL instance. Requires approval in QA and
   Production.
2. **Deploy to Azure App Service** — `Deploy an Azure App Service` built-in
   step using the `TeamBuilder.Api` package.
3. **Smoke test** — `Run a Script` step that calls `/health` and asserts `200`.
4. **Notify** — Optional Slack/Teams notification step.

### Variables by environment

| Variable | Development | QA | Production |
|---|---|---|---|
| `ConnectionStrings__TeamBuilderSql` | LocalDB | Azure SQL QA | Azure SQL Prod |
| `ASPNETCORE_ENVIRONMENT` | Development | QA | Production |
| `AllowedOrigins` | `*` | QA origin | Production origin |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | (optional) | AI QA resource | AI Prod resource |

Store all sensitive values as **Octopus sensitive variables**. Never commit them.

### Approval gates

- QA deployments: optional manual approval.
- Production deployments: required manual approval before each step.
- Migration steps: always require approval before Production.

---

## 5. Application Insights

Add the Application Insights SDK to `TeamBuilder.Api`:

```bash
dotnet add src/TeamBuilder.Api/TeamBuilder.Api.csproj \
  package Microsoft.ApplicationInsights.AspNetCore
```

Register in `Program.cs`:

```csharp
builder.Services.AddApplicationInsightsTelemetry();
```

Set `APPLICATIONINSIGHTS_CONNECTION_STRING` via Azure App Settings or Octopus.
Do **not** commit instrumentation keys.

Recommended telemetry:

- Request duration and status codes (automatic).
- Dependency tracking for SQL queries (automatic with EF Core).
- Custom events for team creation, join request processing, and roster imports.
- Structured logging via `ILogger` (already in place in all controllers).

---

## 6. Security

| Area | Guidance |
|---|---|
| **Secrets** | Never store secrets in source control. Use GitHub Secrets, Azure App Settings, Key Vault references, or Octopus sensitive variables. |
| **Authentication** | JWT Bearer authentication is implemented. Connect a real OIDC provider for staging/production — see [docs/oidc-rollout.md](oidc-rollout.md). |
| **Authorization** | Resource-ownership authorization is implemented for Teams, Events, and RosterImports (403 for non-owners, 409 for null-owner resources). |
| **HTTPS** | Enforce HTTPS-only in Azure App Service. The API already calls `app.UseHttpsRedirection()`. |
| **CodeQL** | Keep GitHub's default CodeQL setup enabled. Do not disable it. |
| **Dependabot** | Keep Dependabot and dependency submission enabled. Review and merge security PRs promptly. |
| **Managed identity** | Use Azure AD managed identity for SQL access where possible to avoid storing SQL credentials. |
| **CORS** | Set `AllowedOrigins` to specific frontend origins in QA and Production. Avoid `*` in non-Development environments. |

---

## 7. Recommended Future PRs

The following are not implemented and should each be a separate focused PR:

| PR | Description | Priority |
|---|---|---|
| **EF Core migrations** | Create `InitialCreate` migration, generate idempotent SQL script, document apply workflow | High |
| **Authentication** | JWT Bearer auth is implemented. Connect a real OIDC provider for QA/Production per [docs/oidc-rollout.md](oidc-rollout.md). | High |
| **Authorization** | Resource-ownership policies are implemented (team owner, event host, roster import owner). | Complete |
| **Data annotations on DTOs** | Add `[Required]`, `[MaxLength]`, `[EmailAddress]` to `CreatePlayerDto`, `CreateTeamDto`, `CreateEventDto`, `CreateRosterImportDto` | Medium |
| **ProducesResponseType improvements** | Replace `typeof(object)` on paginated list endpoints with typed responses | Medium |
| **WebApplicationFactory integration tests** | Controller-level integration tests using in-memory host | Medium |
| **Azure SQL migration smoke test** | CI step that applies migrations against a containerized SQL instance | Medium |
| **RowVersion concurrency integration test** | Verify optimistic concurrency conflicts under real SQL Server | Medium |
| **Health check readiness probe** | Add `/health/ready` distinct from `/health` liveness | Medium |
| **Application Insights telemetry** | Add SDK, configure structured telemetry, add custom events | Medium |
| **Octopus deployment process definition** | Define Octopus project, steps, variables, and lifecycle | Medium |
| **Postman / Newman smoke test** | Run the Postman collection as a CI smoke test against a running API | Low |
| **CORS hardening** | Lock down `AllowedOrigins` for QA and Production environments | Low |
| **Azure Key Vault integration** | Reference secrets via Key Vault rather than App Settings directly | Low |
| **Deployment slot swap** | Configure Blue/Green deployment via Azure App Service staging slot | Low |
