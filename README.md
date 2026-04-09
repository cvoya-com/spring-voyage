# Spring Voyage V2

AI agent orchestration platform built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/)
- [Docker](https://docs.docker.com/get-docker/) or [Podman](https://podman.io/) (for Dapr runtime components)
- Redis running on localhost:6379 (provided automatically by `dapr init`)

## First-Time Setup

```bash
# Install Dapr (choose your container runtime)
dapr init                             # Docker (default)
dapr init --container-runtime podman  # Podman

# Create local secrets file (gitignored, add real secrets as needed)
cd v2
cp dapr/secrets.json.template dapr/secrets.json

# Build
dotnet build

# Run tests
dotnet test
```

## Running Locally

There are two hosts:

- **Worker** (`Host.Worker`) -- runs Dapr actors (AgentActor, UnitActor, etc.). This is the core runtime.
- **API** (`Host.Api`) -- REST API for external access (minimal stub for now).

For local dev, you only need the Worker:

```bash
# Start the Worker with Dapr sidecar
cd v2
dapr run --app-id spring-worker --app-port 5001 \
  --resources-path dapr/components \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001
```

### Testing the Setup

```bash
# Health check (direct to app)
curl http://localhost:5001/health

# Dapr metadata -- verify actor types are registered
curl -s http://localhost:3500/v1.0/metadata | jq '.actorRuntime'

# Send a HealthCheck to an AgentActor (MessageType.HealthCheck = 3)
curl -s -X POST http://localhost:3500/v1.0/actors/AgentActor/test-agent-1/method/ReceiveAsync \
  -H "Content-Type: application/json" \
  -d '{
    "Id": "00000000-0000-0000-0000-000000000001",
    "From": {"Scheme": "human", "Path": "savasp"},
    "To": {"Scheme": "agent", "Path": "test-agent-1"},
    "Type": 3,
    "ConversationId": null,
    "Payload": {},
    "Timestamp": "2026-04-09T00:00:00Z"
  }' | jq

# Send a StatusQuery (MessageType.StatusQuery = 2)
curl -s -X POST http://localhost:3500/v1.0/actors/AgentActor/test-agent-1/method/ReceiveAsync \
  -H "Content-Type: application/json" \
  -d '{
    "Id": "00000000-0000-0000-0000-000000000002",
    "From": {"Scheme": "human", "Path": "savasp"},
    "To": {"Scheme": "agent", "Path": "test-agent-1"},
    "Type": 2,
    "ConversationId": null,
    "Payload": {},
    "Timestamp": "2026-04-09T00:00:00Z"
  }' | jq

# Send a Domain message to start a conversation (MessageType.Domain = 0)
curl -s -X POST http://localhost:3500/v1.0/actors/AgentActor/test-agent-1/method/ReceiveAsync \
  -H "Content-Type: application/json" \
  -d '{
    "Id": "00000000-0000-0000-0000-000000000003",
    "From": {"Scheme": "human", "Path": "savasp"},
    "To": {"Scheme": "agent", "Path": "test-agent-1"},
    "Type": 0,
    "ConversationId": "conv-1",
    "Payload": {},
    "Timestamp": "2026-04-09T00:00:00Z"
  }' | jq
```

### Running Both Hosts (When API Is Needed)

```bash
# Terminal 1: Worker (actors)
cd v2 && dapr run --app-id spring-worker --app-port 5001 \
  --resources-path dapr/components \
  -- dotnet run --project src/Cvoya.Spring.Host.Worker -- --urls http://localhost:5001

# Terminal 2: API (REST endpoints -- stub for now)
cd v2 && dapr run --app-id spring-api --app-port 5000 \
  --resources-path dapr/components \
  -- dotnet run --project src/Cvoya.Spring.Host.Api
```

### Troubleshooting

- **"address already in use"**: The Worker and API both default to port 5000. Use `--urls http://localhost:5001` for the Worker.
- **"secrets.json: no such file"**: Run `cp dapr/secrets.json.template dapr/secrets.json` from the `v2/` directory. Also make sure you run `dapr run` from `v2/` so relative paths resolve correctly.
- **"403 from subscription endpoint"**: Harmless -- Dapr probes for pub/sub subscriptions which are not configured yet.
- **Redis not running**: `dapr init` starts Redis automatically. Check with `docker ps` or `podman ps`.

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
├── dapr/components/                   # Dapr component YAML (Redis, secrets)
├── packages/software-engineering/     # Domain package (agent templates, skills, workflows)
├── docs/                             # Architecture plan and design docs
├── CONVENTIONS.md                     # Coding conventions (mandatory reading)
├── AGENTS.md                          # Agent platform instructions
└── CLAUDE.md                          # Claude Code configuration
```

## Key Concepts

| Concept       | Description                                                                      |
| ------------- | -------------------------------------------------------------------------------- |
| **Agent**     | A single AI entity (Dapr virtual actor) with a mailbox and execution environment |
| **Unit**      | A composite agent -- a group of agents with an orchestration strategy             |
| **Connector** | Bridges an external system (GitHub, Slack, etc.) into a unit                     |
| **Message**   | Typed communication between addressable entities                                 |
| **Skill**     | A prompt fragment + optional tool definitions that an agent can use              |

## Development Workflow

1. Read `CONVENTIONS.md` before writing any code.
2. Read the relevant section of `docs/SpringVoyage-v2-plan.md` for your task.
3. Create a branch and work in a worktree (`git worktree add`).
4. Follow the namespace = folder path convention: `Cvoya.Spring.Core.Messaging` lives in `src/Cvoya.Spring.Core/Messaging/`.
5. Run `dotnet build`, `dotnet test`, and `dotnet format` before committing.
6. Open a PR against `main` -- never push directly.

## Architecture

The platform uses the **Dapr sidecar pattern**. Each host process runs alongside a Dapr sidecar that provides:

- **Actors** -- virtual actor model for agents, units, connectors, and humans
- **Pub/Sub** -- event-driven messaging between components
- **State Store** -- persistent state for actors (Redis for local dev, PostgreSQL in production)
- **Bindings** -- external system integration (webhooks, etc.)

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
   │ Redis    │           │ Redis    │
   │ (pubsub) │           │ (state)  │
   └──────────┘           └──────────┘
```

For the full architecture, see `docs/SpringVoyage-v2-plan.md`.
