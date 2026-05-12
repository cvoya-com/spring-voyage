# Spring Voyage

[![CI](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml/badge.svg)](https://github.com/cvoya-com/spring-voyage/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/License-BSL%201.1-blue.svg)](LICENSE.md)

An open-source collaboration platform for teams of AI agents — and the humans they work with. Built on .NET and Dapr. Agents organize into composable **units**, connect to external systems through pluggable **connectors**, and communicate via typed **messages**. Orchestration is one mechanism inside a unit, not the whole of the platform.

## Key Concepts

| Concept       | Description                                                                      |
| ------------- | -------------------------------------------------------------------------------- |
| **Agent**     | A single AI entity (Dapr virtual actor) with a mailbox and execution environment |
| **Unit**      | An agent that has children (other agents or units); orchestration is runtime behaviour, not platform configuration |
| **Connector** | Bridges an external system (GitHub, Slack, etc.) into a unit                     |
| **Message**   | Typed communication between addressable entities                                 |
| **Skill**     | A prompt fragment + optional tool definitions that an agent can use              |

For the full mental model, see the [Concepts overview](docs/concepts/overview.md).

## Documentation

- [User Guide](docs/guide/overview.md) — using the `spring` CLI and web portal ([Getting Started](docs/guide/intro/getting-started.md))
- [Developer Guide](docs/developer/overview.md) — building, running, and contributing to the platform ([Setup](docs/developer/setup.md), [Operations](docs/developer/operations.md))
- [Deployment Guide](docs/guide/operator/deployment.md) — self-hosting on Docker Compose or Podman (zero-to-running, TLS, secrets, updates)
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

### System configuration

The platform validates its tier-1 configuration (environment variables, `appsettings.json`, mounted secrets) at startup. A mandatory requirement that's missing or malformed (e.g. the PostgreSQL connection string) aborts the host with an actionable error; optional requirements (e.g. the GitHub App credentials) report Disabled or Degraded and the host keeps booting.

Inspect the cached report after startup via the portal page `/system/configuration` or the CLI verb `spring system configuration`. Both surfaces read `GET /api/v1/system/configuration` and render per-subsystem status, env-var names, reasons, and suggested fixes. See [`docs/architecture/configuration.md`](docs/architecture/configuration.md) for the framework contract.

### Default tenant bootstrap

On first start the Worker host materialises the canonical `default` tenant
and invokes every registered `ITenantSeedProvider` against it. The pass is
gated by `Tenancy:BootstrapDefaultTenant` (default `true`); set it to
`false` when tenant provisioning is driven out-of-band. See the
**Multi-tenancy** section of [`docs/architecture/security.md`](docs/architecture/security.md#default-tenant-bootstrap-676)
for the contract every seed provider must honour (idempotent upserts by
`(tenant_id, <natural-key>)`, never overwrite operator edits).

### Connector credentials are optional for startup

The platform starts cleanly without any connector secrets. Connector-specific
credentials — including the GitHub App id and private key
(`GitHub__AppId` / `GitHub__PrivateKeyPem`) — are **connector-gated**: if
unset, the GitHub connector registers in a *disabled with reason* state and
`GET /api/v1/connectors/github/actions/list-installations` returns a
structured `404` the portal and CLI render as "GitHub App not configured"
(issue #609). Set them when you are ready to use the GitHub connector; see
[`docs/guide/operator/deployment.md § Tier-1 platform credentials`](docs/guide/operator/deployment.md#tier-1-platform-credentials--github-app-identity-env-only)
for the expected shape (PEM contents, not a path).

**First-run GitHub bootstrap — recommended path.** Instead of walking the
~10 manual GitHub-docs steps to register a new App and copy its secrets
into `devops/deploy/spring.env`, run one CLI verb:

```bash
spring github-app register --name "Spring Voyage (<your-deployment>)"
```

The verb drives GitHub's [App-from-manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest):
it opens your browser on a pre-filled "create App" page, receives the
conversion code on a loopback listener, and writes `GitHub__AppId`,
`GitHub__PrivateKeyPem`, `GitHub__WebhookSecret`, and the OAuth client
id/secret into `devops/deploy/spring.env`. Pass `--org <slug>` to register
under an organisation or `--write-secrets` to persist via platform-scoped
secrets instead. See [`docs/architecture/cli-and-web.md`](docs/architecture/cli-and-web.md#github-app-bootstrap-verb-631)
for the full flag list.

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

To run the full stack (Postgres, Redis, Dapr control plane, API, Worker, web dashboard, Caddy with automatic TLS) on a single host, use the source-free installer below. Source-clone is still supported for contributors and operators who want to build from `main`.

### Quick install (canonical)

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

The installer downloads the deployment bundle, dispatcher binary, and `spring` CLI for your platform; verifies them against `SHA256SUMS`; pulls the platform image; and brings the stack up. Two prompts only — `DEPLOY_HOSTNAME` (default `localhost`) and an opt-in GitHub-App registration flow. `--yes` skips both. See the [operator deployment guide](docs/guide/operator/deployment.md) for the full walkthrough, flags, and [ADR-0042](docs/decisions/0042-local-operator-installer.md) for the design.

Teardown is symmetric:

```bash
spring-voyage uninstall            # preserves spring.env + host state + workspaces
spring-voyage uninstall --purge    # factory reset
```

### Build from source

If you want to track `main` or use Docker instead of Podman, clone the repo:

```bash
cd devops/deploy/
cp spring.env.example spring.env
$EDITOR spring.env                                # deploy-time config: hostname, DB password, image tags

# Docker Compose
docker compose --env-file spring.env build
docker compose --env-file spring.env up -d

# Or Podman (deploy.sh)
../build/build.sh
./deploy.sh up
# ./deploy.sh clean  # destructive reset: containers, volumes, networks, local images
```

You can skip the build step entirely if you point `SPRING_PLATFORM_IMAGE` and unit execution images at pre-published registry refs; the runtime pulls them on first `up`.

**First-run follow-up: set LLM credentials.** LLM provider API keys are **tier-2 tenant-default credentials**, not deployment config — they do NOT live in `spring.env`. Three paths, pick whichever fits:

```bash
# 1) CLI (recommended for scripts / CI)
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."
spring secret create --scope tenant openai-api-key    --value "sk-..."

# 2) Portal: open Settings → "Tenant defaults" panel → paste + Set

# 3) Inline on unit creation (#626) — pair with `--save-as-tenant-default`
#    to write the key as a tenant default while spinning up the unit:
spring unit create first-team \
  --tool claude-code \
  --api-key-from-file ~/.secrets/anthropic.txt \
  --save-as-tenant-default
#    Or accept the key in the unit-creation wizard's inline input at
#    `/units/create` — the "Save as tenant default" checkbox decides
#    whether the key lands at tenant or unit scope.
```

Units inherit tenant defaults automatically. Override per unit via the Secrets tab on a unit detail page or `spring secret create --scope unit --unit <name> anthropic-api-key --value "..."`. The platform does not read LLM provider keys from environment variables — credentials must be set at tenant or unit scope. See [`docs/guide/secrets.md`](docs/guide/secrets.md) for the full three-tier model and resolution order.

The canonical operator guide is [docs/guide/operator/deployment.md](docs/guide/operator/deployment.md) — it covers the zero-to-running walkthrough, container topology, Dapr components, Postgres/Redis configuration, Caddy + Let's Encrypt, secrets bootstrap, health checks, updates, and troubleshooting. The script-level reference (commands, environment variables, webhook relay, per-user agent networks) lives in [`devops/deploy/README.md`](devops/deploy/README.md).

## CLI

The platform's primary user-facing surface is the `spring` CLI, in `src/Cvoya.Spring.Cli/`:

```bash
# Run from source
dotnet run --project src/Cvoya.Spring.Cli -- <command>

# Or publish a self-contained executable
dotnet publish src/Cvoya.Spring.Cli -c Release -o ./out
./out/spring <command>
```

See the [Getting Started guide](docs/guide/intro/getting-started.md) for a full walkthrough — creating a unit, adding agents, wiring connectors, and sending the first message.

### Custom agent images

An agent dispatches in a container. The platform ships two reference tool-bearing images plus a bridge base image (PR 3b of [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087), [#1096](https://github.com/cvoya-com/spring-voyage/issues/1096)), all built by `./devops/build/build-agent-images.sh`:

| Image | Conformance path | Use it for |
| ----------------- | ---------------- | ---------- |
| `ghcr.io/cvoya-com/claude-code-base:latest` | path 1 (bridge) | Anthropic Claude Code CLI on top of the agent-base bridge. |
| `ghcr.io/cvoya-com/spring-voyage-agent:latest`        | path 3 (native A2A) | Dapr Agent runtime — speaks A2A natively. |
| `ghcr.io/cvoya-com/agent-base:<semver>`                | path 1 base     | Bring your own CLI on top of the bridge sidecar. |

To layer extra tooling on top, the shortest path is a Dockerfile that extends one of the bases. Two starter templates ship under [`devops/build/examples/dockerfiles/`](devops/build/examples/dockerfiles/):

| Template | When to use |
| -------- | ----------- |
| [`minimal-extension`](devops/build/examples/dockerfiles/minimal-extension/) | Re-tag a base image under your own registry. |
| [`custom-tools`](devops/build/examples/dockerfiles/custom-tools/) | Layer extra CLI tools on top of the base. |

Reference the built image through a unit's or agent's `execution.image` field — either from a YAML manifest or through the portal's new **Execution** tab (unit detail / agent detail). The five-field execution block (`image`, `runtime`, `tool`, `provider`, `model`) plus the **agent → unit → fail** resolution chain is described in [`docs/architecture/units.md`](docs/architecture/units.md#unit-execution-defaults-and-the-agent--unit--fail-resolution-chain-601-b-wide).

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

Key routes (see [Web Portal Walkthrough](docs/guide/user/portal.md) for the full list):

- **Dashboard** (`/`) — unit/agent cards, cost overview, real-time activity feed
- **Units** (`/units`, `/units/{id}`) — list, create, and configure units
- **Agents** (`/agents`, `/agents/{id}`) — roster and per-agent detail
- **Activity Feed** (`/activity`) — paginated, filterable event log
- **Engagements** (`/conversations`, `/conversations/{id}`) — thread list and per-thread view
- **Analytics** (`/analytics`) — costs, throughput, and wait-time rollups
- **Packages** (`/packages`) — installed packages and catalog
- **Directory** (`/directory`) — tenant-wide expertise index

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
├── devops/                                   # Build (Dockerfiles, build*.sh), deploy (Podman/compose, Caddy, setup), install (source-free)
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

Spring Voyage follows an open core model. This repository contains the complete, fully functional platform: agents (units are agents that have children), messaging, routing, runtime-decided orchestration with the closed orchestration-tool surface, execution, connectors, CLI, basic auth (API key), ephemeral cloning, observability, basic cost tracking, A2A, unit nesting, package system, and dashboard.

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
- **Free for production** except for offering it as a competing managed AI agent collaboration service
- **Converts to Apache 2.0** on 2030-04-10 (four years from initial release)

See the [LICENSE](LICENSE.md) file for the full terms and the [NOTICE](NOTICE.md) file for third-party attributions.
