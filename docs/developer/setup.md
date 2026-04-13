# Development Setup

This guide covers setting up a local development environment for contributing to Spring Voyage V2.

## Prerequisites

- **.NET SDK** (latest LTS) -- for building the platform
- **Dapr CLI** -- for running the Dapr sidecar locally
- **Podman** (or Docker) -- for Redis, execution environments, and container mode
- **Node.js** -- if working on the web portal

Optional:
- **Python 3.11+** -- if working on Python-based agents

## Quick Start

The fastest way to get the full stack running locally:

```bash
# Build
dotnet build

# Start all services (container mode)
./scripts/dev.sh up

# Or start in process mode (hot-reload, debugger attach)
./scripts/dev.sh up --process
```

Container mode runs everything in podman containers. Process mode runs
Redis in podman and the .NET hosts via `dapr run` + `dotnet run` — useful
when you want hot-reload or need to attach a debugger.

```bash
# Check status
./scripts/dev.sh status

# Follow logs
./scripts/dev.sh logs api

# Stop everything
./scripts/dev.sh down
```

See `scripts/dev.sh --help` for all commands and environment variables.

## Building

```
# Build the entire solution
dotnet build SpringVoyage.slnx

# Build a specific project
dotnet build src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj
```

## Running Manually

If you prefer to start services individually instead of using `dev.sh`:

### Start Infrastructure

Start Redis using a container:

```
podman run -d --name spring-dev-redis -p 6379:6379 redis:7
```

### Initialize Dapr

```
dapr init
```

This installs the Dapr sidecar and default components.

### Start the Worker

```bash
dapr run --app-id spring-worker --app-port 5001 \
  --dapr-http-port 3500 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001
```

### Start the API

```bash
dapr run --app-id spring-api --app-port 5000 \
  --dapr-http-port 3501 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Api -- --local
```

The `--local` flag enables single-tenant mode with no authentication.

### Start the Web Dashboard

```bash
cd src/Cvoya.Spring.Web
npm install
npm run dev
```

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

Dapr components for local development are in `dapr/components/local/` — see
[`dapr/README.md`](../../dapr/README.md) for the full layout and commands:

- `dapr/components/local/` — localhost Redis + env-var secret store (used by `dapr run`).
- `dapr/config/local.yaml` — Dapr Configuration (tracing, features) for development.

Pass the matching directory to `dapr run` with `--resources-path dapr/components/local`
and `--config dapr/config/local.yaml`.

Production Dapr components live in the private Spring repository alongside
the deployment scripts.

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
5. Test end-to-end with `./scripts/dev.sh up`
6. Open a PR against `main`
