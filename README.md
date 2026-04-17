# Spring Voyage

[![CI](https://github.com/savasp/spring-voyage/actions/workflows/ci.yml/badge.svg)](https://github.com/savasp/spring-voyage/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1-blue.svg)](LICENSE.md)

AI agent orchestration platform built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**.

## Key Concepts

| Concept       | Description                                                                      |
| ------------- | -------------------------------------------------------------------------------- |
| **Agent**     | A single AI entity (Dapr virtual actor) with a mailbox and execution environment |
| **Unit**      | A composite agent -- a group of agents with an orchestration strategy             |
| **Connector** | Bridges an external system (GitHub, Slack, etc.) into a unit                     |
| **Message**   | Typed communication between addressable entities                                 |
| **Skill**     | A prompt fragment + optional tool definitions that an agent can use              |

For the full mental model, see the [Concepts overview](docs/concepts/overview.md).

## Documentation

- [User Guide](docs/guide/overview.md) — using the `spring` CLI and web portal ([Getting Started](docs/guide/getting-started.md))
- [Developer Guide](docs/developer/overview.md) — building, running, and contributing to the platform ([Setup](docs/developer/setup.md), [Operations](docs/developer/operations.md))
- [Deployment Guide](docs/guide/deployment.md) — self-hosting on Docker Compose or Podman (zero-to-running, TLS, secrets, updates)
- [Architecture](docs/architecture/README.md) — how the concepts are realized as a running system
- [Documentation index](docs/README.md) — concepts, architecture, user guide, developer guide, and reference

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — to build the platform
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/) — to run the Dapr sidecar locally
- [Podman](https://podman.io/) (or [Docker](https://docs.docker.com/get-docker/)) — for execution environments and workflow containers
- **PostgreSQL** — primary data store (can run in a container; see below)
- **Redis** — local pub/sub and state store (provided automatically by `dapr init`, or run in a container)
- [jq](https://jqlang.github.io/jq/) — used by the `curl` examples below

Optional:

- **Node.js** — only if working on the web dashboard (`src/Cvoya.Spring.Web/`)
- **Python 3.11+** — only if working on Python-based agents

This list mirrors [`docs/developer/setup.md`](docs/developer/setup.md), which stays the canonical reference.

## Quick Start

```bash
# Install Dapr (choose your container runtime)
dapr init                             # Docker (default)
dapr init --container-runtime podman  # Podman

# Start PostgreSQL (skip if you already have one running on localhost:5432)
podman run -d --name spring-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17

# The local Dapr profile uses secretstores.local.env — export any secrets
# (e.g. POSTGRES_PASSWORD, REDIS_PASSWORD) in the shell that runs `dapr run`.
# See dapr/README.md for details.

# Build
dotnet build SpringVoyage.slnx

# Run tests
dotnet test SpringVoyage.slnx
```

For the full local-dev loop (API + Worker + dashboard), see [`docs/developer/setup.md`](docs/developer/setup.md).

## Running Locally

There are two hosts that run side-by-side with Dapr sidecars:

- **Worker** (`Cvoya.Spring.Host.Worker`) — runs Dapr actors (`AgentActor`, `UnitActor`, etc.) and owns database migrations. This is the core runtime.
- **API** (`Cvoya.Spring.Host.Api`) — REST API for external access (CLI, dashboard, integrations).

```bash
# Terminal 1: Worker (actors + migrations)
dapr run --app-id spring-worker --app-port 5001 \
  --dapr-http-port 3500 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001

# Terminal 2: API (REST endpoints)
dapr run --app-id spring-api --app-port 5000 \
  --dapr-http-port 3501 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Api
```

### Testing the Setup

```bash
# Health check
curl http://localhost:5001/health

# Dapr metadata -- verify actor types are registered
curl -s http://localhost:3500/v1.0/metadata | jq '.actorRuntime'
```

For Dapr component layout (local vs. production profiles, secret stores, configs), see [`dapr/README.md`](dapr/README.md). For platform operations (health checks, database migrations, troubleshooting, DataProtection), see [`docs/developer/operations.md`](docs/developer/operations.md).

## Self-Hosting

To run the full stack (Postgres, Redis, Dapr control plane, API, Worker, web dashboard, Caddy with automatic TLS) on a single workstation or VPS, use the container-based deployment under [`deployment/`](deployment/README.md) instead of `dapr run`. Both Docker Compose and a Podman-native script are supported:

```bash
cd deployment/
cp spring.env.example spring.env
$EDITOR spring.env                                # fill in secrets, hostname, image tags

# Docker Compose
docker compose --env-file spring.env build
docker compose --env-file spring.env up -d

# Or Podman (deploy.sh)
./deploy.sh build
./deploy.sh up
```

You can skip the build step entirely if you point `SPRING_PLATFORM_IMAGE` / `SPRING_AGENT_IMAGE` in `spring.env` at pre-published images in a registry; the runtime pulls them on first `up`. For remote VPS deployments, `deploy-remote.sh` wraps SSH + rsync and supports the same registry flow via `SPRING_SKIP_SOURCE_SYNC=1`.

The canonical operator guide is [docs/guide/deployment.md](docs/guide/deployment.md) — it covers the zero-to-running walkthrough, container topology, Dapr components, Postgres/Redis configuration, Caddy + Let's Encrypt, secrets bootstrap, health checks, updates, and troubleshooting. The script-level reference (commands, environment variables, webhook relay, per-user agent networks) lives in [`deployment/README.md`](deployment/README.md).

## CLI

The platform's primary user-facing surface is the `spring` CLI, in `src/Cvoya.Spring.Cli/`:

```bash
# Run from source
dotnet run --project src/Cvoya.Spring.Cli -- <command>

# Or publish a self-contained executable
dotnet publish src/Cvoya.Spring.Cli -c Release -o ./out
./out/spring <command>
```

See the [Getting Started guide](docs/guide/getting-started.md) for a full walkthrough — creating a unit, adding agents, wiring connectors, and sending the first message.

## Web Dashboard

The web dashboard is a React/Next.js + TypeScript application at `src/Cvoya.Spring.Web/`.

```bash
cd src/Cvoya.Spring.Web
npm install       # install dependencies
npm run dev       # dev server at http://localhost:3000
npm run build     # standalone Next.js build in .next/standalone/
npm test          # run component tests (Vitest)
```

The dashboard calls the API host for data. Set `NEXT_PUBLIC_API_URL` to point at the running API:

```bash
NEXT_PUBLIC_API_URL=http://localhost:5000 npm run dev
```

**Stack:** Next.js 16, React 19, TypeScript 5.8, Tailwind CSS 4.1

**Pages:**

- **Dashboard** (`/`) — agent list, unit list, cost overview, real-time activity feed
- **Activity Feed** (`/activity`) — real-time event stream via SSE
- **Agent Detail** (`/agents?id=name`) — status, cost breakdown, clones
- **Unit Detail** (`/units?id=name`) — members, cost, orchestration status

The dashboard consumes the API host endpoints. For local development, start the API host on port 5000 and the dashboard dev server on port 3000.

## Project Structure

```
├── src/
│   ├── Cvoya.Spring.Core/                    # Domain interfaces and types (no external dependencies)
│   ├── Cvoya.Spring.Dapr/                    # Dapr actor implementations
│   ├── Cvoya.Spring.Connectors.Abstractions/ # Connector contracts
│   ├── Cvoya.Spring.Connector.GitHub/        # GitHub connector
│   ├── Cvoya.Spring.Host.Api/                # ASP.NET Core Web API host
│   ├── Cvoya.Spring.Host.Worker/             # Headless worker host (Dapr actor runtime, owns migrations)
│   ├── Cvoya.Spring.Cli/                     # CLI ("spring" command)
│   ├── Cvoya.Spring.A2A/                     # A2A protocol
│   ├── Cvoya.Spring.Manifest/                # Package/manifest tooling
│   └── Cvoya.Spring.Web/                     # Web dashboard (React/Next.js)
├── tests/                                    # xUnit test projects
├── dapr/                                     # Dapr components + config (local/production profiles)
├── deployment/                               # Podman Compose, Caddy, deploy.sh
├── packages/                                 # Domain packages (software-engineering, product-management)
├── docs/                                     # Concepts, architecture, user guide, developer guide
├── CONVENTIONS.md                            # Coding conventions (mandatory reading)
└── AGENTS.md                                 # Agent platform instructions
```

A more detailed layout (including how Core/Dapr separate, where strategies live, and how packages are organized) is in [`docs/developer/overview.md`](docs/developer/overview.md).

## Architecture

The platform uses the **Dapr sidecar pattern**. Each host process runs alongside a Dapr sidecar that provides:

- **Actors** -- virtual actor model for agents, units, connectors, and humans
- **Pub/Sub** -- event-driven messaging between components
- **State Store** -- persistent state for actors (Redis for local dev)
- **Bindings** -- external system integration (webhooks, etc.)

For the full architecture, start at [`docs/architecture/README.md`](docs/architecture/README.md). Browse all documentation at [`docs/README.md`](docs/README.md).

## Open Core Model

Spring Voyage follows an open core model. This repository contains the complete, fully functional platform: agents, messaging, routing, orchestration (AI + Workflow strategies), execution, connectors, CLI, basic auth (API key), ephemeral cloning, observability, basic cost tracking, A2A, unit nesting, package system, and dashboard.

Commercial extensions (multi-tenancy, OAuth/SSO/SAML, billing, and advanced features) are developed separately and are not part of this repository.

## Contributing

We welcome contributions! Please read:

- [CONTRIBUTING.md](CONTRIBUTING.md) -- development workflow and CLA
- [docs/developer/setup.md](docs/developer/setup.md) -- prerequisites, building, running locally
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) -- community standards
- [SECURITY.md](SECURITY.md) -- reporting security issues
- [CONVENTIONS.md](CONVENTIONS.md) -- coding patterns (mandatory)

## License

Spring Voyage is licensed under the [Business Source License 1.1](LICENSE.md).

**What this means:**

- **Free to use** for personal projects, development, testing, and internal non-production use
- **Free for production** except for offering it as a competing managed AI agent orchestration service
- **Converts to Apache 2.0** on 2030-04-10 (four years from initial release)

See the [LICENSE](LICENSE.md) file for the full terms and the [NOTICE](NOTICE.md) file for third-party attributions.
