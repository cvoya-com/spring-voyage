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
dotnet build src/Cvoya.Spring.Host.Worker/Cvoya.Spring.Host.Worker.csproj
```

For local builds of container images (platform, agent, sidecar) use
`eng/build/build.sh`.

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

### Start the hosts

Spring Voyage runs two .NET hosts with explicit roles (see
[Components](../architecture/components.md)):

- `spring-api` — the stateless HTTP front door (REST API, webhooks, OpenAPI).
- `spring-worker` — the execution host: Dapr actors, A2A dispatch, the platform
  MCP server, and EF Core migrations.

Each gets its own Dapr sidecar. The Worker owns database migrations, so start it
first (or accept that the API trusts the schema is already in place).

```
# Worker host
dapr run --app-id spring-worker --app-port 5100 --dapr-http-port 3600 \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --local

# API host
dapr run --app-id spring-api --app-port 5000 --dapr-http-port 3500 \
  -- dotnet run --project src/Cvoya.Spring.Host.Api -- --local
```

The `--local` flag enables local-dev mode with no authentication. For the
container-based deployment (`deploy.sh`), both hosts and their sidecars are
started for you — see [Platform Operations](operations.md).

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
dotnet test tests/unit/Cvoya.Spring.Core.Tests/

# With Dapr integration tests (requires Dapr sidecar)
dotnet test tests/unit/Cvoya.Spring.Dapr.Tests/ --filter Category=Integration
```

## Building Container Images

Reference agent-runtime and platform images are built locally with
`eng/build/build.sh`, which uses Podman and writes the same canonical
`ghcr.io/cvoya-com/*` refs that release builds publish. Production deployments
pull pre-built images from GHCR by tag; see [Releases](releases.md).

## Dapr Component Configuration

Dapr components are split into two profiles — see [`eng/dapr/README.md`](../../eng/dapr/README.md)
for the full layout and commands:

- `eng/dapr/components/local/` — localhost Redis + env-var secret store (used by `dapr run`).
- `eng/dapr/components/production/` — Podman-hosted Postgres + Redis, secrets via
  `secretstores.local.env` backed by `eng/config/spring.env`.
- `eng/dapr/config/local.yaml`, `eng/dapr/config/production.yaml` — Dapr Configuration
  (tracing, features) for each profile.

Pass the matching directory to `dapr run` with `--resources-path eng/dapr/components/local`
and `--config eng/dapr/config/local.yaml`.

## Database Migrations

Schema changes use EF Core migrations. `SpringDbContext` lives in
`Cvoya.Spring.Dapr`, so `dotnet ef` always targets that project:

```
# Add a new migration
dotnet tool restore
dotnet ef migrations add <MigrationName> \
  --project src/Cvoya.Spring.Dapr \
  --output-dir Data/Migrations

# Apply migrations to a real database
dotnet ef database update --project src/Cvoya.Spring.Dapr \
  --connection "Host=...;Database=...;Username=...;Password=..."
```

The Worker host auto-applies pending migrations on startup, so a fresh local
database comes up with an up-to-date schema without running `dotnet ef`
manually. See [Platform Operations § Database Migrations](operations.md#database-migrations)
for the auto-migrate flag, multi-replica coordination, and idempotent SQL scripts.

## Development Workflow

1. Create a branch for your work
2. Make changes to the relevant projects
3. Write tests (unit tests in `tests/`, integration tests with Dapr where needed)
4. Build and run tests locally
5. Test end-to-end with the local API host
6. Open a PR against `main`
