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

TeamBuilder API can run as a Docker Web Service on Render for QA validation before production rollout.

Live QA behavior on Render:
- `GET /` returns `200 OK` with `TeamBuilder API Running`
- `GET /health` is the Render health check endpoint and currently returns `Healthy`
- `GET /health/ready` may remain `Unhealthy` until database/readiness dependencies are configured
- `GET /swagger` currently returns `404` in QA if Swagger is not enabled there
- HTTPS redirection is disabled in QA to avoid Render reverse-proxy HTTPS port warnings
- Production should still use HTTPS redirection

| Setting | Value |
|---|---|
| **Service name** | `teambuilder-api-qa` |
| **Render URL** | `https://teambuilder-api-qa.onrender.com` |
| **Runtime** | Docker |
| **Environment** | QA |
| **Production URL** | `https://teambuilder.info` |

### Required Render environment variables

| Variable | Value | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `QA` | Keep the app in QA mode. |
| `ASPNETCORE_URLS` | `http://0.0.0.0:${PORT}` | Bind to Render's assigned port. |
| `AllowedOrigins` | `https://teambuilder.info,https://teambuilder-api-qa.onrender.com` | Allow production UI and QA API access. |
| `Jwt__Authority` | `https://login.microsoftonline.com/299120a7-9680-48a3-b1ad-150125d656ce/v2.0` | Entra authority for QA JWT validation. |
| `Jwt__Audience` | `api://5457c4d7-0746-4337-ab67-c5c1061b2963` | API audience value. |
| `Jwt__Issuer` | `https://login.microsoftonline.com/299120a7-9680-48a3-b1ad-150125d656ce/v2.0` | Entra issuer for QA JWT validation. |
| `Jwt__PlayerIdClaim` | `sub` | Player identifier claim. |
| `Jwt__RequireHttpsMetadata` | `true` | Keep metadata retrieval secure. |
| `Jwt__SigningKey` | *(do not set)* | Do not set for Entra/OIDC JWT validation. |
| `ConnectionStrings__DefaultConnection` | *(do not set yet)* | Leave unset until the real database is ready. |

---

## Octopus Deploy Variables

Define the following variables in your Octopus Deploy project:

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

### Environment Variable

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | QA, Production | ASP.NET Core environment name | `QA` or `Production` |

### JWT / OIDC Variables

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `Jwt__Authority` | QA, Production | Entra/OIDC authority URL | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Jwt__Audience` | QA, Production | API audience | `api://teambuilder-api` |
| `Jwt__Issuer` | QA, Production | Token issuer | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Jwt__PlayerIdClaim` | QA, Production | Claim mapped to Player ID | `oid` or `sub` |
| `Jwt__RequireHttpsMetadata` | QA, Production | Require HTTPS for metadata discovery | `true` |
| `Jwt__SigningKey` | QA, Production | Do not set for Entra/OIDC JWT validation | *(not used)* |

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
cd src/TeamBuilder.Infrastructure
dotnet ef migrations add InitialCreate --startup-project ../TeamBuilder.Api
dotnet ef database update --startup-project ../TeamBuilder.Api
```

### QA/Production

**Option 1: Apply migrations during deployment (Octopus Deploy step)**

Add a deployment step that runs:

```bash
dotnet ef database update --startup-project TeamBuilder.Api --configuration Release
```

**Option 2: Generate SQL scripts and review before applying**

```bash
dotnet ef migrations script --startup-project ../TeamBuilder.Api --idempotent --output migration.sql
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
  cd TeamBuilder.Api
  dotnet ef database update --no-build
  ```

#### Step 4: Health Check

- Step Type: **HTTP - Test URL**
- URL: `https://#{DeploymentUrl}/health`
- Expected Status: `200 OK`

#### Step 5: Production host and CORS guidance

- **Production public URL**: `https://teambuilder.info`
- **Production AllowedOrigins**: `https://teambuilder.info`
- **Support email**: `support@teambuilder.info`
- **QA host placeholder**: `https://qa.teambuilder.info` if a QA host is provisioned; otherwise keep the value TBD until the host exists

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
- [ ] Swagger UI is accessible (Development/QA only)
- [ ] Database connection is successful
- [ ] CORS configuration allows expected frontend origins
- [ ] Logging is working (check Application Insights or file logs)

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

- Enable HTTPS redirection (already configured)
- Configure CORS to allow only known frontend origins
- Add authentication and authorization before going live
- Consider API rate limiting for production

---

## Monitoring

### Application Insights (Recommended)

Configure Application Insights connection string in Octopus variables:

```json
{
  "ApplicationInsights": {
    "ConnectionString": "#{ApplicationInsights.ConnectionString}"
  }
}
```

Add the Application Insights SDK package:

```bash
dotnet add src/TeamBuilder.Api package Microsoft.ApplicationInsights.AspNetCore
```

### Health Check Monitoring

Set up monitoring tools to poll the `/health` endpoint:

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

**Error**: "An unhandled exception occurred"

**Solution**: Check Application Insights or server logs for detailed error messages.

---

## Rollback Strategy

If a deployment fails:

1. **Rollback Code**: Use Octopus Deploy's "Redeploy previous release" feature
2. **Rollback Database**: If migrations were applied, manually revert using migration rollback:
   ```bash
   dotnet ef database update <PreviousMigrationName> --startup-project ../TeamBuilder.Api
   ```

---

## Contact

For deployment support, contact the DevOps team or open an issue on [GitHub](https://github.com/RocketDelivery2/TeamBuilder/issues).
