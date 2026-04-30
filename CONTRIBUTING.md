# Contributing to TeamBuilder

Thank you for your interest in contributing to TeamBuilder! This document outlines the process for contributing to the project.

## Code of Conduct

Be respectful and constructive in all interactions. We are committed to making TeamBuilder a welcoming project for everyone.

## How to Contribute

### Reporting Bugs

- Search existing issues before opening a new one.
- Use the GitHub issue tracker to report bugs.
- Include steps to reproduce, expected behavior, actual behavior, and your environment.

### Suggesting Features

- Open a GitHub issue describing the feature and its motivation.
- Keep suggestions focused and actionable.

### Submitting Pull Requests

1. Fork the repository.
2. Create a feature branch from `main`:
   ```bash
   git checkout -b feature/my-feature
   ```
3. Make your changes following the coding standards below.
4. Ensure all tests pass:
   ```bash
   dotnet test
   ```
5. Commit with a clear message:
   ```bash
   git commit -m "Add my feature"
   ```
6. Push your branch and open a Pull Request against `main`.

## Coding Standards

- Follow existing code style and naming conventions (PascalCase for public members, camelCase for locals).
- Write unit tests for new features and bug fixes; all tests must pass.
- Keep changes focused — one feature or fix per pull request.
- Update API documentation or README if your change affects public behavior.
- Do not commit secrets, connection strings, or sensitive configuration values.
- Ensure the CI pipeline passes before requesting review.

## Architecture Overview

TeamBuilder uses Clean Architecture with four layers:

| Layer | Project | Responsibility |
|-------|---------|----------------|
| API | `TeamBuilder.Api` | Controllers, middleware, Swagger |
| Application | `TeamBuilder.Application` | DTOs, service interfaces, business contracts |
| Domain | `TeamBuilder.Domain` | Entities, enums (no framework dependencies) |
| Infrastructure | `TeamBuilder.Infrastructure` | EF Core, SQL Server, service implementations |

Dependencies only flow inward — Infrastructure and Api depend on Application, Application depends on Domain.

## Development Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone the repository and restore dependencies:
   ```bash
   git clone https://github.com/RocketDelivery2/TeamBuilder.git
   cd TeamBuilder
   dotnet restore
   ```
3. Run the tests to confirm a working baseline:
   ```bash
   dotnet test
   ```
4. See [README.md](README.md) for full local development setup including database configuration.

## Security

Do not include security vulnerability details in public issues or pull requests. See [SECURITY.md](SECURITY.md) for the responsible disclosure process.
