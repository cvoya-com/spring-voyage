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

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/)
- [Docker](https://docs.docker.com/get-docker/) or [Podman](https://podman.io/) (for Dapr runtime components)
- [jq](https://jqlang.github.io/jq/) (for testing commands below)
- Redis running on localhost:6379 (provided automatically by `dapr init`)

## Quick Start

```bash
# Install Dapr (choose your container runtime)
dapr init                             # Docker (default)
dapr init --container-runtime podman  # Podman

# The local Dapr profile uses secretstores.local.env — export any secrets
# (e.g. POSTGRES_PASSWORD, REDIS_PASSWORD) in the shell that runs `dapr run`.
# See dapr/README.md for details.

# Build
dotnet build

# Run tests
dotnet test
```

## Running Locally

There are two hosts:

- **Worker** (`Host.Worker`) -- runs Dapr actors (AgentActor, UnitActor, etc.). This is the core runtime.
- **API** (`Host.Api`) -- REST API for external access.

For local dev, you only need the Worker:

```bash
dapr run --app-id spring-worker --app-port 5001 \
  --dapr-http-port 3500 \
  --resources-path dapr/components/local \
  --config dapr/config/local.yaml \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001
```

### Testing the Setup

```bash
# Health check
curl http://localhost:5001/health

# Dapr metadata -- verify actor types are registered
curl -s http://localhost:3500/v1.0/metadata | jq '.actorRuntime'
```

### Running Both Hosts

```bash
# Terminal 1: Worker (actors)
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

## Web Dashboard

The web dashboard is a React/Next.js + TypeScript application at `src/Cvoya.Spring.Web/`.

```bash
cd src/Cvoya.Spring.Web
npm install       # install dependencies
npm run dev       # dev server at http://localhost:3000
npm run build     # static export to out/
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
│   ├── Cvoya.Spring.Core/              # Domain interfaces and types (no external dependencies)
│   ├── Cvoya.Spring.Dapr/              # Dapr actor implementations
│   ├── Cvoya.Spring.Connector.GitHub/  # GitHub connector
│   ├── Cvoya.Spring.Host.Api/          # ASP.NET Core Web API host
│   ├── Cvoya.Spring.Host.Worker/       # Headless worker host (Dapr actor runtime)
│   ├── Cvoya.Spring.Cli/              # CLI ("spring" command)
│   ├── Cvoya.Spring.A2A/              # A2A protocol (stub)
│   └── Cvoya.Spring.Web/             # Web dashboard (React/Next.js)
├── tests/                             # xUnit test projects
├── dapr/                              # Dapr components + config (local/prod profiles)
├── packages/software-engineering/     # Domain package (agent templates, skills, workflows)
├── docs/                             # Architecture plan and design docs
├── CONVENTIONS.md                     # Coding conventions (mandatory reading)
└── AGENTS.md                          # Agent platform instructions
```

## Architecture

The platform uses the **Dapr sidecar pattern**. Each host process runs alongside a Dapr sidecar that provides:

- **Actors** -- virtual actor model for agents, units, connectors, and humans
- **Pub/Sub** -- event-driven messaging between components
- **State Store** -- persistent state for actors (Redis for local dev)
- **Bindings** -- external system integration (webhooks, etc.)

For the full architecture, see `docs/SpringVoyage-v2-plan.md`. Browse all documentation at [docs/README.md](docs/README.md).

## Open Core Model

Spring Voyage follows an open core model. This repository contains the complete, fully functional platform: agents, messaging, routing, orchestration (AI + Workflow strategies), execution, connectors, CLI, basic auth (API key), ephemeral cloning, observability, basic cost tracking, A2A, unit nesting, package system, and dashboard.

Commercial extensions (multi-tenancy, OAuth/SSO/SAML, billing, and advanced features) are developed separately and are not part of this repository.

## Contributing

We welcome contributions! Please read:

- [CONTRIBUTING.md](CONTRIBUTING.md) -- development workflow and CLA
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
