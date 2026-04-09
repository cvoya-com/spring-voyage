# API and Hosting

This document describes the API surface, hosting modes, deployment topologies, and the CLI.

## Hosting Architecture

Spring Voyage V2 has two host binaries built on the same core libraries:

- **API Host** -- the web application serving REST, WebSocket, and SSE endpoints. Handles authentication, authorization, and multi-tenant routing.
- **Worker Host** -- a headless process hosting actor runtimes for background processing. No external-facing API.

Both hosts share the same core libraries (`Spring.Core`, `Spring.Dapr`) and the same behavior. The difference is what they expose: the API host has an HTTP surface; the worker host does not.

### Local Development Mode

The API Host supports a single-tenant, auth-disabled mode (`--local`) for local development. This is the same binary -- not a separate "daemon" -- just configured to bypass authentication and assume a single implicit tenant.

## API Surface

| Domain | Operations |
|--------|-----------|
| **Identity and Auth** | OAuth login, API token CRUD, token invalidation, tenant user management |
| **Unit Management** | CRUD, configure AI/policies/connectors, manage members |
| **Agent Management** | CRUD, view status, configure expertise |
| **Messaging** | Send messages to agents/units, read conversations |
| **Activity Streams** | Subscribe via SSE/WebSocket |
| **Workflow Management** | Start/stop/inspect, approve human-in-the-loop steps |
| **Directory and Discovery** | Query expertise, browse capabilities |
| **Package Management** | Install/remove, browse registry |
| **Observability** | Metrics, cost tracking, audit logs |
| **Admin** | User management, tenant config |

## Deployment Topologies

| Environment | Topology |
|-------------|---------|
| **Local dev** | API Host (single-tenant mode) + Dapr sidecar + Podman containers. Single machine. |
| **Staging / small prod** | API Host + Worker Host behind a reverse proxy. Docker Compose with Dapr sidecars. PostgreSQL + Redis. |
| **Production** | Kubernetes with Dapr operator. API Host replicas behind load balancer. Worker Hosts scaled by workload. Execution environments as ephemeral pods. Kafka for pub/sub. Azure Key Vault for secrets. |

## The `spring` CLI

The CLI is produced by the `Spring.Cli` project and distributed as:

- **dotnet tool** -- `dotnet tool install -g spring-cli` (requires .NET SDK)
- **Standalone executable** -- self-contained single-file app (no .NET SDK required), distributed via GitHub releases, Homebrew, or direct download

The command name is `spring` in both cases.

### CLI Command Categories

The CLI covers the full API surface:

- **Unit and agent management** -- create, configure, list, delete
- **Messaging** -- send messages, read conversations
- **Activity** -- stream real-time activity, check agent status
- **Connectors** -- add, authenticate, configure
- **Cost** -- view cost summaries and budgets
- **Build** -- build container images from package Dockerfiles
- **Apply** -- apply declarative YAML configurations
- **Workflow** -- inspect and manage running workflows
- **Auth** -- authenticate, manage API tokens
- **Dashboard** -- open the web portal

See the [CLI User Guide](../guide/overview.md) for full command reference.

## Solution Structure

The .NET solution is organized as:

| Project | Purpose |
|---------|---------|
| `Spring.Core` | Domain interfaces and types. No Dapr dependency. |
| `Spring.Dapr` | Dapr implementations of Core interfaces (actors, orchestration strategies) |
| `Spring.A2A` | A2A protocol client and server |
| `Spring.Connector.GitHub` | GitHub connector |
| `Spring.Connector.Slack` | Slack connector |
| `Spring.Host.Api` | API host (REST, WebSocket, SSE, auth, multi-tenant) |
| `Spring.Host.Worker` | Headless worker host |
| `Spring.Cli` | CLI tool |
| `Spring.Web` | Web portal |

Python components (optional, for Python-based agents):

| Module | Purpose |
|--------|---------|
| `spring_agent` | Python agent process (or Dapr Agents-based) |
| `spring_connectors` | Python-side connector logic |

## Platform Administration

Platform operators use a separate admin CLI (`spring-admin`) for:

- Tenant provisioning and management
- Platform health and metrics
- Database migrations
- Version upgrades
- Resource quota management

See the [Developer Guide](../developer/overview.md) for platform operations details.
