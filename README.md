# ENMA

ENMA is a multi-tenant SaaS platform for law firm management.

## Current stack

- .NET 10
- ASP.NET Core Minimal APIs
- Entity Framework Core
- PostgreSQL
- Docker
- xUnit
- Testcontainers

## Current capabilities

- Organization domain model
- Organization creation
- Organization retrieval by ID
- PostgreSQL persistence
- Unique slug protection
- ProblemDetails error responses
- Unit, persistence, and HTTP integration tests

## Repository structure

- `src/Enma.Domain`: entities and business rules
- `src/Enma.Application`: use cases and abstractions
- `src/Enma.Infrastructure`: persistence and external integrations
- `src/Enma.Api`: HTTP entry point and application composition
- `tests/Enma.UnitTests`: domain and application unit tests
- `tests/Enma.IntegrationTests`: persistence and HTTP integration tests

## Local development

Prerequisites:

- .NET 10 SDK
- Docker Desktop
- PowerShell

Review `.env.example`, then prepare the local environment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\setup-local.ps1"
```

Start the API:

```powershell
dotnet run --project ".\src\Enma.Api\Enma.Api.csproj"
```

PostgreSQL runs through Docker Compose. The setup script creates `.env` from the
local example when needed, stores the API connection string in .NET User Secrets,
restores the local .NET tools, and applies the existing migrations. Git ignores
`.env`. Production API startup does not apply migrations automatically.

## Validation

Run the standard validation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\verify.ps1"
```

Include integration tests with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\verify.ps1" -IntegrationTests
```

Integration tests require Docker.

## Deployment

The current supported production ingress and edge security contract is defined
in [docs/deployment/production-topology.md](docs/deployment/production-topology.md).

## API endpoints

- `POST /api/organizations`
- `GET /api/organizations/{id}`
