# Spring Voyage V2

AI agent orchestration platform built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/) (for running locally)
- [Docker](https://docs.docker.com/get-docker/) or [Podman](https://podman.io/) (for Dapr components)
- PostgreSQL (for state store — Dapr manages the connection)
- Redis (for pub/sub — Dapr manages the connection)

## Quick Start

```bash
# 1. Build everything
cd v2
dotnet build

# 2. Run tests
dotnet test

# 3. Check formatting
dotnet format --verify-no-changes

# 4. Initialize Dapr (first time only)
dapr init

# 5. Run the API host with Dapr sidecar
dapr run --app-id spring-api --app-port 5000 -- dotnet run --project src/Cvoya.Spring.Host.Api
```

## Project Structure

```
v2/
├── src/
│   ├── Cvoya.Spring.Core/              # Domain interfaces and types (no external dependencies)
│   ├── Cvoya.Spring.Dapr/              # Dapr actor implementations
│   ├── Cvoya.Spring.Connector.GitHub/  # GitHub connector
│   ├── Cvoya.Spring.Host.Api/          # ASP.NET Core Web API host
│   ├── Cvoya.Spring.Host.Worker/       # Headless worker host (Dapr actor runtime)
│   ├── Cvoya.Spring.Cli/              # CLI ("spring" command)
│   ├── Cvoya.Spring.A2A/              # A2A protocol (stub)
│   └── Cvoya.Spring.Web/             # Web UI (stub)
├── tests/                             # xUnit test projects
├── dapr/components/                   # Dapr component YAML (Redis, PostgreSQL, secrets)
├── packages/software-engineering/     # Domain package (agent templates, skills, workflows)
├── docs/                             # Architecture plan and design docs
├── CONVENTIONS.md                     # Coding conventions (mandatory reading)
├── AGENTS.md                          # Agent platform instructions
└── CLAUDE.md                          # Claude Code configuration
```

## Key Concepts

| Concept | Description |
|---------|-------------|
| **Agent** | A single AI entity (Dapr virtual actor) with a mailbox and execution environment |
| **Unit** | A composite agent — a group of agents with an orchestration strategy |
| **Connector** | Bridges an external system (GitHub, Slack, etc.) into a unit |
| **Message** | Typed communication between addressable entities |
| **Skill** | A prompt fragment + optional tool definitions that an agent can use |

## Development Workflow

1. Read `CONVENTIONS.md` before writing any code.
2. Read the relevant section of `docs/SpringVoyage-v2-plan.md` for your task.
3. Create a branch and work in a worktree (`git worktree add`).
4. Follow the namespace = folder path convention: `Cvoya.Spring.Core.Messaging` lives in `src/Cvoya.Spring.Core/Messaging/`.
5. Run `dotnet build`, `dotnet test`, and `dotnet format` before committing.
6. Open a PR against `main` — never push directly.

## Architecture

The platform uses the **Dapr sidecar pattern**. Each host process runs alongside a Dapr sidecar that provides:

- **Actors** — virtual actor model for agents, units, connectors, and humans
- **Pub/Sub** — event-driven messaging between components
- **State Store** — persistent state for actors (PostgreSQL)
- **Bindings** — external system integration (webhooks, etc.)

```
┌─────────────────┐     ┌─────────────────┐
│   Host.Api      │     │   Host.Worker   │
│  (Web API)      │     │  (Actor runtime)│
│                 │     │                 │
│  ┌───────────┐  │     │  ┌───────────┐  │
│  │ Dapr      │  │     │  │ Dapr      │  │
│  │ Sidecar   │◄─┼─────┼─►│ Sidecar   │  │
│  └───────────┘  │     │  └───────────┘  │
└─────────────────┘     └─────────────────┘
         │                       │
         ▼                       ▼
   ┌──────────┐           ┌──────────┐
   │ Redis    │           │PostgreSQL│
   │ (pubsub) │           │ (state)  │
   └──────────┘           └──────────┘
```

For the full architecture, see `docs/SpringVoyage-v2-plan.md`.
