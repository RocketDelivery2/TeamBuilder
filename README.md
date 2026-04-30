# TeamBuilder

**Need a team? Build one. Join one. Keep one going.**

TeamBuilder is an open-source project focused on solving the mechanics of building and maintaining teams for anything: video games, sports, pickup games, clubs, events, competitions, and beyond.

The mission is simple: **maximum playtime, minimum downtime**.

TeamBuilder is designed to make it quick and simple for hosts to create team-based events, players to find teams, and teams to refill empty spots when someone leaves. Long term, TeamBuilder aims to support a common roster language so other team-building apps, websites, uploads, and APIs can work together through shared roster data.

Whether you are organizing a basketball pickup game, building a world-first video game group, hosting a club event, or trying to keep a team from falling apart when someone has to leave, TeamBuilder exists to help keep the team moving.

Together, let's put the **Capital T** in **Team**.

---

## Overview

A modern, API-first platform for building, hosting, and managing teams and events. TeamBuilder provides a clean RESTful API that can serve any frontend framework, mobile app, desktop application, or third-party integration.

## Architecture Overview

TeamBuilder is built using **Clean Architecture** principles with clear separation of concerns:

```
┌─────────────────────────────────────────────────────────┐
│                    Client Layer                          │
│  (React, Angular, Vue, Blazor, Mobile, Desktop, etc.)   │
└───────────────────────┬─────────────────────────────────┘
                        │ HTTP/REST
┌───────────────────────▼─────────────────────────────────┐
│              TeamBuilder.Api (Web API)                   │
│  Controllers, Swagger/OpenAPI, CORS, Health Checks       │
└───────────────────────┬─────────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────────┐
│          TeamBuilder.Application                         │
│     DTOs, Interfaces, Business Contracts                 │
└─────────────┬─────────────────────┬─────────────────────┘
              │                     │
┌─────────────▼──────────┐   ┌──────▼──────────────────────┐
│  TeamBuilder.Domain    │   │ TeamBuilder.Infrastructure  │
│  Entities, Enums       │   │ EF Core, SQL Server, Services│
└────────────────────────┘   └─────────────────────────────┘
```

### API-First Middle Layer

The ASP.NET Core Web API acts as the **middle layer** between any client-side frontend and the SQL Server backend:

- **Frontend-Agnostic**: The API returns JSON and has no dependency on any specific UI framework
- **Multi-Client Support**: Designed to serve React, Angular, Vue, Blazor WASM, mobile apps, desktop apps, and third-party integrations simultaneously
- **RESTful Design**: All endpoints follow REST principles with proper HTTP verbs, status codes, and resource patterns
- **Versioned Routes**: API endpoints are under `/api/v1/` for future versioning flexibility
- **Swagger/OpenAPI**: Interactive API documentation at `/swagger` in Development mode

## Core Features

### Team Management
- Create, update, and delete teams
- Set team capacity and track current member count
- Team status management (Active, Recruiting, Full, Inactive, Disbanded)
- Region and category classification
- Tag-based organization

### Event Management
- Host team events with date, location, and participant limits
- Event status tracking (Scheduled, InProgress, Completed, Cancelled)
- Category and tag organization
- Roster entries for event participants

### Player Management
- Player profiles with username, display name, email, bio, avatar
- Region-based player organization
- Track team memberships and hosted events
- Join request management

### Join Request Flow
- Players request to join teams
- Team owners/admins approve or reject requests
- Automatic team member creation on approval
- Automatic status updates (Recruiting → Full) when team reaches capacity
- Refill support: when a member leaves a full team, status changes back to Recruiting

### Team Membership
- Players can join multiple teams
- Role-based membership (Owner, Admin, Member)
- Leave team functionality with automatic refill support
- Active/inactive member tracking

### Roster Import
- Import player rosters from external sources (CSV, etc.)
- Process roster data to create/update player records
- Track import status and processing notes
- Support for multiple roster source types

## Technology Stack

- **.NET 10**: Latest .NET platform
- **ASP.NET Core Web API**: RESTful API framework
- **Entity Framework Core 10**: Code First ORM
- **SQL Server / Azure SQL**: Production-ready database
- **xUnit**: Unit testing framework
- **FluentAssertions**: Expressive test assertions
- **Swashbuckle**: Swagger/OpenAPI documentation

## Project Structure

```
TeamBuilder/
├── src/
│   ├── TeamBuilder.Api/              # Web API presentation layer
│   │   ├── Controllers/              # REST API controllers
│   │   ├── appsettings.json          # Default configuration
│   │   ├── appsettings.Development.json
│   │   ├── appsettings.QA.json       # QA with Octopus placeholders
│   │   └── appsettings.Production.json # Prod with Octopus placeholders
│   ├── TeamBuilder.Application/      # Application layer
│   │   ├── DTOs/                     # Data transfer objects
│   │   ├── Interfaces/               # Service contracts
│   │   └── Models/                   # Shared models (pagination, etc.)
│   ├── TeamBuilder.Domain/           # Domain layer
│   │   ├── Entities/                 # Business entities
│   │   └── Enums/                    # Domain enumerations
│   └── TeamBuilder.Infrastructure/   # Infrastructure layer
│       ├── Data/                     # EF Core DbContext
│       │   └── Configurations/       # Entity type configurations
│       └── Services/                 # Service implementations
├── tests/
│   └── TeamBuilder.Tests/            # Unit and integration tests
│       ├── Domain/                   # Domain entity tests
│       └── Application/              # Service tests
├── docs/
│   └── deployment.md                 # Deployment guide
└── README.md
```

## API Endpoints

### Teams
- `GET /api/v1/teams/{id}` - Get team by ID
- `GET /api/v1/teams?page=1&pageSize=20&category={category}&region={region}&status={status}` - List teams with filtering and pagination
- `POST /api/v1/teams` - Create new team
- `PUT /api/v1/teams/{id}` - Update team
- `DELETE /api/v1/teams/{id}` - Delete team
- `POST /api/v1/teams/{teamId}/members/{playerId}/leave` - Leave team (refill support)

### Events
- `GET /api/v1/events/{id}` - Get event by ID
- `GET /api/v1/events?page=1&pageSize=20&category={category}&region={region}&status={status}` - List events
- `POST /api/v1/events` - Create new event
- `PUT /api/v1/events/{id}` - Update event
- `DELETE /api/v1/events/{id}` - Delete/cancel event

### Players
- `GET /api/v1/players/{id}` - Get player by ID
- `GET /api/v1/players/username/{username}` - Get player by username
- `GET /api/v1/players?page=1&pageSize=20&region={region}` - List players
- `POST /api/v1/players` - Create new player
- `PUT /api/v1/players/{id}` - Update player
- `DELETE /api/v1/players/{id}` - Delete player

### Join Requests
- `GET /api/v1/joinrequests/{id}` - Get join request by ID
- `GET /api/v1/joinrequests/teams/{teamId}?status={status}` - Get requests for a team
- `GET /api/v1/joinrequests/players/{playerId}?status={status}` - Get requests by a player
- `POST /api/v1/joinrequests` - Create join request
- `PUT /api/v1/joinrequests/{id}/process` - Approve/reject join request

### Roster Imports
- `GET /api/v1/rosterimports/{id}` - Get roster import by ID
- `GET /api/v1/rosterimports?page=1&pageSize=20&isProcessed={true|false}` - List imports
- `POST /api/v1/rosterimports` - Upload roster data
- `PUT /api/v1/rosterimports/{id}/process` - Process roster import
- `DELETE /api/v1/rosterimports/{id}` - Delete import

### Health
- `GET /health` - Health check endpoint (includes SQL Server connectivity)

## Getting Started

### Prerequisites

- .NET 10 SDK (10.0.201 or later)
- SQL Server 2019+ or Azure SQL Database (for production)
- SQL Server LocalDB (for development)
- Visual Studio 2026, VS Code, or Rider

### Local Development Setup

1. **Clone the repository**
   ```bash
   git clone https://github.com/RocketDelivery2/TeamBuilder.git
   cd TeamBuilder
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Update connection string** (optional)

   Development uses LocalDB by default. To use a different database, update `src/TeamBuilder.Api/appsettings.Development.json`:
   ```json
   {
     "ConnectionStrings": {
       "TeamBuilderSql": "Server=YOUR_SERVER;Database=TeamBuilder;Trusted_Connection=True;"
     }
   }
   ```

4. **Create database migration**
   ```bash
   cd src/TeamBuilder.Infrastructure
   dotnet ef migrations add InitialCreate --startup-project ../TeamBuilder.Api
   dotnet ef database update --startup-project ../TeamBuilder.Api
   ```

5. **Run the API**
   ```bash
   cd ../TeamBuilder.Api
   dotnet run
   ```

6. **Access Swagger UI**

   Open browser to: `https://localhost:5001/swagger`

### Running Tests

```bash
dotnet test
```

All 35 unit tests validate:
- Domain entity behavior
- Team/Player/Event CRUD operations
- Join request workflow (create, approve, reject)
- Team membership (join, leave, refill)
- Roster import processing
- Pagination and filtering
- Edge cases and error handling

## Configuration & Deployment

### Environment Configuration

TeamBuilder supports three environments with separate configuration files:

- **Development** (`appsettings.Development.json`): Safe defaults for local development
- **QA** (`appsettings.QA.json`): Octopus Deploy variable placeholders for QA environment
- **Production** (`appsettings.Production.json`): Octopus Deploy variable placeholders for production

### Octopus Deploy Variables

The following Octopus Deploy variables are expected for QA and Production:

| Variable | Description | Example |
|----------|-------------|---------|
| `AzureSql.ServerName` | Azure SQL Server hostname | `teambuilder-qa.database.windows.net` |
| `AzureSql.DatabaseName` | Database name | `TeamBuilderQA` |
| `AzureSql.UserName` | SQL authentication username | `teambuilder-api` |
| `AzureSql.Password` | SQL authentication password (sensitive) | `***` |
| `AllowedOrigins` | Comma-separated CORS origins | `https://app.teambuilder.com,https://admin.teambuilder.com` |
| `ApplicationInsights.ConnectionString` | Azure Application Insights connection string | `InstrumentationKey=...` |
| `ASPNETCORE_ENVIRONMENT` | Environment name | `QA` or `Production` |

**Important**: No secrets are committed to source control. All sensitive values use Octopus placeholder syntax: `#{VariableName}`

### Database Migrations

1. **Create migration**
   ```bash
   dotnet ef migrations add MigrationName --startup-project src/TeamBuilder.Api --project src/TeamBuilder.Infrastructure
   ```

2. **Apply migration** (Development)
   ```bash
   dotnet ef database update --startup-project src/TeamBuilder.Api --project src/TeamBuilder.Infrastructure
   ```

3. **Generate SQL script** (for production deployment)
   ```bash
   dotnet ef migrations script --startup-project src/TeamBuilder.Api --project src/TeamBuilder.Infrastructure --idempotent --output migration.sql
   ```

For detailed deployment instructions, see [docs/deployment.md](docs/deployment.md).

## Azure SQL / EF Core Code First

TeamBuilder uses **Entity Framework Core Code First** approach:

- Domain entities are defined in `TeamBuilder.Domain/Entities/`
- Database schema is derived from entity configurations in `TeamBuilder.Infrastructure/Data/Configurations/`
- Migrations track schema changes over time
- SQL Server provider is configured for Azure SQL compatibility
- Connection string is read from configuration key: `ConnectionStrings:TeamBuilderSql`

### Database Design Highlights

- **UTC timestamps** for all date/time fields
- **Optimistic concurrency** using RowVersion
- **Indexes** on commonly queried fields:
  - Event date, category, status, region
  - Team status, category, region
  - Join request status
  - Created timestamps
- **Relationships**:
  - Teams → Owner (Player)
  - Teams → Members (TeamMember)
  - Teams → JoinRequests
  - Teams → Events
  - Players → TeamMemberships
  - Events → RosterEntries

## Clean Architecture Benefits

1. **Domain Independence**: Core business entities have zero dependencies on frameworks
2. **Testability**: Business logic is easily tested without infrastructure concerns
3. **Flexibility**: Swap EF Core for Dapper or SQL without touching domain/application layers
4. **Maintainability**: Clear boundaries and single responsibility
5. **API-First**: Frontend teams can work independently using the API contract

## Security Considerations

- **HTTPS Redirection**: Enabled in production
- **CORS**: Configurable per environment; use specific origins in production (not `*`)
- **Sensitive Data**: Exception details hidden outside Development
- **Authentication**: Ready for integration with Identity, JWT, or Azure AD (not yet implemented)
- **Secrets Management**: No hardcoded secrets; all sensitive config uses Octopus variables
- **Input Validation**: DTOs validate incoming requests; ModelState checked in controllers

## Performance & Scalability

- **Async/await** throughout for non-blocking I/O
- **Pagination** on all list endpoints (default 20 items, max 100)
- **DTO projections** to minimize data transfer
- **Database indexes** on query fields
- **Health checks** for monitoring and load balancer readiness
- **Stateless API** design for horizontal scaling
- **SQL Server connection pooling** via EF Core

## Future Enhancements

Documented for future implementation:

- [ ] Authentication & authorization (JWT, Identity, Azure AD)
- [ ] Rate limiting and throttling
- [ ] Distributed caching (Redis)
- [ ] Background job processing (Hangfire, Azure Functions)
- [ ] Real-time notifications (SignalR)
- [ ] File upload for avatars and roster imports
- [ ] Advanced search with Elasticsearch
- [ ] Multi-tenancy support
- [ ] Audit logging
- [ ] API versioning (v2, v3)

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit changes: `git commit -m 'Add my feature'`
4. Push to branch: `git push origin feature/my-feature`
5. Create a Pull Request

### Coding Standards

- Follow existing code style and naming conventions
- Write unit tests for new features
- Ensure all tests pass: `dotnet test`
- Update documentation for API changes
- No hardcoded secrets or connection strings

## License

This project is proprietary software. All rights reserved.

## Support

For questions or issues, please contact the development team or create an issue in the repository.

