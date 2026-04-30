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
| `AllowedOrigins` | QA, Production | Comma-separated list of allowed origins | `https://app-qa.teambuilder.com,https://admin-qa.teambuilder.com` |

### Application Insights (Optional)

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `ApplicationInsights.ConnectionString` | QA, Production | Azure Application Insights connection string | `InstrumentationKey=...` |

### Environment Variable

| Variable | Scope | Description | Example |
|----------|-------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | QA, Production | ASP.NET Core environment name | `QA` or `Production` |

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
