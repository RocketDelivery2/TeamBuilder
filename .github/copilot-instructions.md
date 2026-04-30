# GitHub Copilot Instructions for TeamBuilder

## Project Overview

TeamBuilder is an API-first .NET platform for building, hosting, and managing teams and events. It uses Clean Architecture with four layers: Api, Application, Domain, and Infrastructure.

## Architecture

- **TeamBuilder.Api** – ASP.NET Core Web API controllers, middleware, Swagger/OpenAPI, health checks.
- **TeamBuilder.Application** – DTOs, service interfaces, business contracts. No framework dependencies beyond abstractions.
- **TeamBuilder.Domain** – Pure C# entities and enums. Zero infrastructure or framework dependencies.
- **TeamBuilder.Infrastructure** – EF Core 10 Code First, SQL Server / Azure SQL provider, service implementations.
- **TeamBuilder.Tests** – xUnit unit tests using FluentAssertions, Moq, and EF Core InMemory.

Dependencies flow inward only: Api → Application ← Infrastructure, Domain ← Application.

## Technology Stack

- .NET 10 / C# 13
- ASP.NET Core Web API
- Entity Framework Core 10 (Code First, SQL Server provider)
- Azure SQL in QA and Production; LocalDB in Development
- xUnit, FluentAssertions, Moq, coverlet
- Octopus Deploy for QA/Production deployments (variable placeholders in appsettings)

## Coding Conventions

- Use PascalCase for types, methods, and public members; camelCase for local variables and parameters.
- Enable nullable reference types (`#nullable enable` / `<Nullable>enable</Nullable>`).
- Use `async`/`await` throughout; return `Task<T>` for all I/O operations.
- Validate DTOs via data annotations; check `ModelState` in controllers.
- Paginate all list endpoints using the shared `PagedResult<T>` / pagination models in Application.
- Route all API endpoints under `/api/v1/`.
- Never commit secrets, connection strings, or sensitive configuration values.
- Configuration secrets use Octopus Deploy placeholder syntax `#{VariableName}` in QA/Production appsettings.

## Testing Guidelines

- Place unit tests in `tests/TeamBuilder.Tests`.
- Use EF Core InMemory provider for repository/service tests; use Moq for interface mocks.
- Use FluentAssertions (`result.Should().Be(...)`) rather than raw xUnit assertions where possible.
- Each test class should focus on a single unit of behavior (arrange-act-assert pattern).

## Security

- Do not add hardcoded secrets, passwords, API keys, or connection strings.
- Do not add deployment steps or environment-specific secrets to GitHub Actions workflows.
- HTTPS redirection is enabled in production; do not disable it.
- CORS origins must be explicit in QA/Production (no wildcard `*`).
- Report security issues privately per [SECURITY.md](../SECURITY.md).

## CI/CD

- CI runs on every push and pull request to `main` via `.github/workflows/ci.yml`.
- CI steps: `dotnet restore` → `dotnet build --configuration Release` → `dotnet test`.
- CodeQL security scanning runs weekly and on push/PR via `.github/workflows/codeql.yml`.
- Dependabot keeps NuGet packages and GitHub Actions up to date weekly.
- Deployments to QA and Production are handled by Octopus Deploy (not GitHub Actions).
