# Development Setup

This guide covers setting up a local development environment for contributing to Spring Voyage.

## Prerequisites

- **.NET SDK** (latest LTS) -- for building the platform
- **Dapr CLI** -- for running the Dapr sidecar locally
- **Podman** (or Docker) -- for execution environments and workflow containers
- **PostgreSQL** -- for the primary data store (can run in a container)
- **Redis** -- for local pub/sub (can run in a container)

Optional:
- **Python 3.11+** -- if working on Python-based agents
- **Node.js** -- if working on the web portal

## Building

```
# Build the entire solution
dotnet build SpringVoyage.slnx

# Build a specific project
dotnet build src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj
```

## Running Locally

### Start Infrastructure

Start PostgreSQL and Redis using containers or local installations. For example, with Podman:

```
podman run -d --name spring-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17
podman run -d --name spring-redis -p 6379:6379 redis:7
```

Or use Docker equivalents. If you already have PostgreSQL and Redis running locally, skip this step.

### Initialize Dapr

```
dapr init
```

This installs the Dapr sidecar and default components.

### Start the API Host

```
dapr run --app-id spring-api --app-port 5000 --dapr-http-port 3500 \
  -- dotnet run --project src/Cvoya.Spring.Host.Api -- --local
```

The `--local` flag enables single-tenant mode with no authentication.

### Use the CLI

```
# The CLI connects to localhost by default in local mode
spring unit list
spring agent status
```

## Running Tests

```
# All tests
dotnet test SpringVoyage.slnx

# A specific test project
dotnet test tests/Cvoya.Spring.Core.Tests/

# With Dapr integration tests (requires Dapr sidecar)
dotnet test tests/Cvoya.Spring.Dapr.Tests/ --filter Category=Integration
```

## Building Container Images

Package Dockerfiles produce container images for workflows and execution environments:

```
# Build all images for a package
spring build packages/software-engineering

# Build a specific workflow
spring build packages/software-engineering/workflows/software-dev-cycle

# List built images
spring images list
```

## Dapr Component Configuration

Dapr components are split into two profiles — see [`dapr/README.md`](../../dapr/README.md)
for the full layout and commands:

- `dapr/components/local/` — localhost Redis + env-var secret store (used by `dapr run`).
- `dapr/components/production/` — Podman-hosted Postgres + Redis, secrets via
  `secretstores.local.env` backed by `deployment/spring.env`.
- `dapr/config/local.yaml`, `dapr/config/production.yaml` — Dapr Configuration
  (tracing, features) for each profile.

Pass the matching directory to `dapr run` with `--resources-path dapr/components/local`
and `--config dapr/config/local.yaml`.

## Database Migrations

Schema changes use EF Core migrations:

```
# Add a new migration
dotnet ef migrations add <MigrationName> --project src/Cvoya.Spring.Host.Api

# Apply migrations
dotnet ef database update --project src/Cvoya.Spring.Host.Api

# Or via the admin CLI
spring-admin migrate
```

## Development Workflow

1. Create a branch for your work
2. Make changes to the relevant projects
3. Write tests (unit tests in `tests/`, integration tests with Dapr where needed)
4. Build and run tests locally
5. Test end-to-end with the local API host
6. Open a PR against `main`
