# TeamBuilder Deployment Guide

## Overview

This document explains how to deploy TeamBuilder to different environments using Octopus Deploy and Azure SQL Server.

---

## Environment Strategy

TeamBuilder supports three environments:

1. **Development** - Local developer machines
2. **QA** - Quality assurance/testing environment
3. **Production** - Live production environment

Each environment has its own configuration file that is selected based on the `ASPNETCORE_ENVIRONMENT` variable.

---

## Configuration Files

### Development (`appsettings.Development.json`)

Used for local development. Contains safe connection strings for LocalDB or local SQL Server.

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=(localdb)\\mssqllocaldb;Database=TeamBuilderDev;Trusted_Connection=True;MultipleActiveResultSets=true"
  },
  "AllowedOrigins": "http://localhost:3000,http://localhost:4200"
}
```

### QA (`appsettings.QA.json`)

Uses Octopus Deploy variable substitution. Variables are replaced during deployment.

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=#{AzureSql.ServerName};Database=#{AzureSql.DatabaseName};User Id=#{AzureSql.UserName};Password=#{AzureSql.Password};..."
  },
  "AllowedOrigins": "#{AllowedOrigins}"
}
```

### Production (`appsettings.Production.json`)

Uses Octopus Deploy variable substitution. Variables are replaced during deployment.

```json
{
  "ConnectionStrings": {
    "TeamBuilderSql": "Server=#{AzureSql.ServerName};Database=#{AzureSql.DatabaseName};User Id=#{AzureSql.UserName};Password=#{AzureSql.Password};..."
  },
  "AllowedOrigins": "#{AllowedOrigins}",
  "ApplicationInsights": {
    "ConnectionString": "#{ApplicationInsights.ConnectionString}"
  }
}
```

---

## Render QA Hosting

This section records a possible QA hosting configuration only. Repository
contents do not verify that a Render service, URL, Entra application, or live
environment variables currently exist or are configured as shown. Confirm
provider-side state before relying on these values.

In the application, `GET /health` is a liveness check that does not contact
external dependencies. `GET /health/ready` checks SQL Server connectivity via
the configured `ConnectionStrings:TeamBuilderSql`. Swagger is enabled only in
Development. HTTPS redirection is disabled in the `QA` environment.

| Setting | Value |
|---|---|
| **Suggested service name** | `teambuilder-api-qa` (unverified) |
| **Recorded Render URL** | `https://teambuilder-api-qa.onrender.com` (unverified) |
| **Example runtime** | Docker (unverified) |
| **Example environment** | QA (unverified) |
| **Recorded production URL** | `https://teambuilder.info` (unverified) |

### Example Render environment variables

| Variable | Value | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `QA` | Keep the app in QA mode. |
| `ASPNETCORE_URLS` | `http://0.0.0.0:${PORT}` | Bind to Render's assigned port. |
| `AllowedOrigins` | `https://teambuilder.info,https://teambuilder-api-qa.onrender.com` | Illustrative only; replace with confirmed origins. |
| `Jwt__Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | Set to the confirmed OIDC authority. |
| `Jwt__Audience` | `<api-audience>` | Set to the audience expected by the API registration. |
| `Jwt__Issuer` | *(optional)* | Expected issuer on the symmetric-key validation path; OIDC uses authority metadata. |
| `Jwt__ExternalIdentity__SubjectClaim` | `sub` by default; `oid` for Entra when present in the API token | Configured opaque external subject claim; not an internal player ID. |
| `Jwt__ExternalIdentity__TenantIdClaim` | `tid` | Optional tenant metadata claim. |
| `Jwt__ExternalIdentity__Provider` | `oidc` by default | Descriptive provider metadata label. |
| `Jwt__RequireHttpsMetadata` | `true` | Keep metadata retrieval secure. |
| `Jwt__SigningKey` | *(do not set)* | Do not set for Entra/OIDC JWT validation. |
| `ConnectionStrings__TeamBuilderSql` | *(set when SQL is provisioned)* | The API reads this exact connection-string key. |

---

## Octopus Deploy Variables

The checked-in environment appsettings files use these variable names. Their
presence does not verify that an Octopus project, environment, or variable is
currently configured. Example values below are illustrative and must be
confirmed with the deployment operators.

### Azure SQL Variables

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `AzureSql.ServerName` | QA, Production | Azure SQL Server hostname | `teambuilder-qa.database.windows.net` |
| `AzureSql.DatabaseName` | QA, Production | Database name | `TeamBuilderQA` |
| `AzureSql.UserName` | QA, Production | SQL authentication username | `teambuilder-api` |
| `AzureSql.Password` | QA, Production | SQL authentication password (sensitive) | `********` |

### CORS Variables

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `AllowedOrigins` | QA, Production | Comma-separated list of allowed origins | `https://qa.teambuilder.info` (QA) / `https://teambuilder.info` (Production) |

### Application Insights (Optional)

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `ApplicationInsights.ConnectionString` | QA, Production | Azure Application Insights connection string | `InstrumentationKey=...` |

The connection-string placeholder is present in the environment appsettings
files, but the API project does not currently register the Application
Insights SDK. The placeholder alone does not enable telemetry.

### Environment Variable

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | QA, Production | ASP.NET Core environment name | `QA` or `Production` |

### JWT / OIDC Variables

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `Jwt__Authority` | QA, Production | Entra/OIDC authority URL | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Jwt__Audience` | QA, Production | API audience | `api://teambuilder-api` |
| `Jwt__Issuer` | QA, Production | Expected issuer for symmetric-key validation; OIDC uses authority metadata | *(provider-specific)* |
| `Jwt__RequireHttpsMetadata` | QA, Production | Require HTTPS for metadata discovery | `true` |
| `Jwt__SigningKey` | Local development / tests | If non-empty, selects symmetric-key validation instead of OIDC | Keep secrets outside source control |
| `Jwt__ExternalIdentity__SubjectClaim` | Per provider | Configured opaque external subject claim | `sub` by default; `oid` for Entra when issued |
| `Jwt__ExternalIdentity__TenantIdClaim` | Per provider | Optional tenant metadata claim | `tid` by default |
| `Jwt__ExternalIdentity__Provider` | Per provider | Descriptive identity-provider metadata | `oidc` by default |

The API supports symmetric-key and OIDC-authority JWT validation, selected by
configuration. The `ExternalIdentity` scheme is the default. A validated
token's exact issuer and configured subject claim resolve through
`PlayerIdentity` to the internal `Player.Id`; no JWT claim is parsed as a
player ID. The repository does not establish which identity provider or
credentials are currently deployed; validate the authority, issuer, audience,
and subject claim with the provider actually in use.

---

## Azure SQL Server Setup

### 1. Create Azure SQL Server

```bash
az sql server create \
  --name teambuilder-sql-server \
  --resource-group teambuilder-rg \
  --location eastus \
  --admin-user sqladmin \
  --admin-password <SecurePassword>
```

### 2. Create Database

```bash
az sql db create \
  --resource-group teambuilder-rg \
  --server teambuilder-sql-server \
  --name TeamBuilderQA \
  --service-objective S1
```

### 3. Configure Firewall Rules

```bash
# Allow Azure services
az sql server firewall-rule create \
  --resource-group teambuilder-rg \
  --server teambuilder-sql-server \
  --name AllowAzureServices \
  --start-ip-address 0.0.0.0 \
  --end-ip-address 0.0.0.0

# Allow specific IP (for management)
az sql server firewall-rule create \
  --resource-group teambuilder-rg \
  --server teambuilder-sql-server \
  --name AllowMyIP \
  --start-ip-address <Your-IP> \
  --end-ip-address <Your-IP>
```

### 4. Create SQL User for API

The names and commands below are examples, not evidence that these Azure SQL
resources or credentials exist in any environment.

Connect to the database and run:

```sql
CREATE LOGIN [teambuilder-api] WITH PASSWORD = '<SecurePassword>';
CREATE USER [teambuilder-api] FOR LOGIN [teambuilder-api];
EXEC sp_addrolemember 'db_owner', 'teambuilder-api';
```

---

## Database Migrations

### Local Development

```bash
dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api
```

The initial and subsequent migrations are already committed under
`src/TeamBuilder.Infrastructure/Persistence/Migrations/`. Do not recreate an
`InitialCreate` migration. Add a new named migration only when changing the
EF Core model, then review and commit it.

### QA/Production

**Option 1: Apply migrations during deployment (Octopus Deploy step)**

Add a deployment step that runs:

```bash
dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api --configuration Release
```

**Option 2: Generate SQL scripts and review before applying**

```bash
dotnet ef migrations script --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api --idempotent --output migration.sql
```

Review the `migration.sql` file and apply it manually or through a deployment pipeline.

---

## Octopus Deploy Setup

### 1. Create Octopus Project

- Project Name: **TeamBuilder**
- Lifecycle: Standard (Dev → QA → Production)

### 2. Define Variables

Add all variables listed in the "Octopus Deploy Variables" section above.

Mark sensitive variables (passwords, connection strings) as **Sensitive**.

### 3. Deployment Process

#### Step 1: Deploy Package

- Step Type: **Deploy a Package**
- Package ID: `TeamBuilder.Api`
- Target Role: `web-server`

#### Step 2: Configure IIS (if using IIS)

- Step Type: **Deploy to IIS**
- Website Name: `TeamBuilder`
- App Pool: `.NET v10.0`
- Binding: `https://*:443`

#### Step 3: Apply Migrations (optional)

- Step Type: **Run a Script**
- Script:
  ```bash
  dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api --configuration Release --no-build
  ```

#### Step 4: Health Check

- Step Type: **HTTP - Test URL**
- URL: `https://#{DeploymentUrl}/health`
- Expected Status: `200 OK`

#### Step 5: Production host and CORS guidance

- The URLs and support address below are recorded examples only; confirm
  provider-side configuration before using them.
- **Recorded production public URL**: `https://teambuilder.info` (unverified)
- **Example Production AllowedOrigins**: `https://teambuilder.info` (unverified)
- **Recorded support email**: `support@teambuilder.info` (unverified)
- **Example QA host**: `https://qa.teambuilder.info` (unverified)

---

## Deployment Checklist

### Before Deployment

- [ ] All Octopus variables are defined for the target environment
- [ ] Azure SQL Server is created and firewall rules are configured
- [ ] Database user has appropriate permissions
- [ ] SSL certificate is installed (for HTTPS)
- [ ] Application Insights resource is created (if using monitoring)

### After Deployment

- [ ] API is accessible at the deployment URL
- [ ] Health check endpoint (`/health`) returns 200 OK
- [ ] Swagger UI is accessible in Development (disabled outside Development)
- [ ] Database connection is successful
- [ ] CORS configuration allows expected frontend origins
- [ ] Logging is working in the configured hosting environment (Application Insights is not currently wired into the API)

---

## Security Considerations

### Secrets Management

- **Never commit secrets** to source control
- Store secrets in Octopus Deploy as sensitive variables
- Use Azure Key Vault for production secrets (optional enhancement)
- Rotate passwords and connection strings regularly

### Connection String Security

- Use SQL authentication with strong passwords
- Consider using Azure Managed Identity instead of SQL authentication
- Enable Azure SQL Advanced Threat Protection
- Use SSL/TLS for all connections (default with Azure SQL)

### API Security

- HTTPS redirection is configured except in the `QA` environment.
- Configure CORS to allow only known frontend origins
- JWT bearer validation uses the default `ExternalIdentity` scheme and
  endpoint-specific PlayerIdentity-based authorization. Player onboarding and
  self-profile mutations require authentication; public player discovery
  omits Email. Join-request reads are applicant/team-owner restricted, and
  roster-import reads are importer-only.
- Consider API rate limiting for production

---

## Monitoring

### Application Insights (Not currently wired)

An Application Insights connection-string placeholder exists in the environment
configuration, but Application Insights telemetry is not currently wired into
the API. Do not treat this setting as proof that telemetry is being collected.

```json
{
  "ApplicationInsights": {
    "ConnectionString": "#{ApplicationInsights.ConnectionString}"
  }
}
```

### Health Check Monitoring

Poll `/health` for process liveness and `/health/ready` to verify the configured
SQL Server dependency. The liveness endpoint intentionally does not check SQL.

- Azure Monitor
- Datadog
- New Relic
- Custom monitoring scripts

---

## Troubleshooting

### Migration Fails

**Error**: "Cannot connect to database"

**Solution**: Verify connection string, firewall rules, and SQL user permissions.

### CORS Errors

**Error**: "Access to fetch at '...' has been blocked by CORS policy"

**Solution**: Add the frontend origin to the `AllowedOrigins` variable in Octopus Deploy.

### API Returns 500 Error

**Client response**: `An unexpected error occurred.`

**Solution**: Inspect the server-side logs available in the hosting environment
for the original exception. Application Insights is not currently wired into
the API. The API does not return internal exception messages in unexpected 500
responses.

---

## Rollback Strategy

If a deployment fails:

1. **Rollback Code**: Use Octopus Deploy's "Redeploy previous release" feature
2. **Rollback Database**: If migrations were applied, manually revert using migration rollback:
   ```bash
   dotnet ef database update <PreviousMigrationName> --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api
   ```

---

## Contact

For deployment support, contact the DevOps team or open an issue on [GitHub](https://github.com/RocketDelivery2/TeamBuilder/issues).
