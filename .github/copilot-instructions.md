# TeamBuilder Copilot Instructions

TeamBuilder is an API-first, frontend-agnostic .NET platform for building,
hosting, joining, maintaining, and refilling teams.

The ASP.NET Core Web API is the middle layer between any client-side
frontend and the backend data platform.

The backend direction is:

- C#
- ASP.NET Core Web API
- EF Core Code First
- Azure SQL Server
- clean architecture
- DTO-based API contracts
- xUnit tests
- GitHub Actions
- Octopus Deploy-ready configuration
- Development, QA, and Production environment separation

When making changes:

- Do not hardcode secrets.
- Do not commit connection strings, API keys, Azure SQL credentials, or Octopus keys.
- Do not disable CodeQL, Dependabot, or secret scanning.
- Do not commit directly to main.
- Keep controllers thin.
- Keep business logic out of controllers.
- Use DTOs for API requests and responses.
- Do not expose EF entities directly from API endpoints.
- Use async EF Core calls.
- Use pagination for list endpoints.
- Preserve frontend-agnostic API design.
- Add or update tests for behavior changes.
- Prefer small, reviewable pull requests.
