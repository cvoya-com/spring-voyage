# Developer Guide

This guide is for developers contributing to the Spring Voyage platform itself -- the .NET infrastructure, connectors, workflows, agents, and tooling.

## Document Map

| Document | Description |
|----------|-------------|
| [Development Setup](setup.md) | Prerequisites, building, running locally |
| [Creating Packages](creating-packages.md) | Building domain packages: agents, skills, workflows, connectors |
| [Platform Operations](operations.md) | Running locally, health checks, and troubleshooting |

## Project Layout

```
SpringVoyage.slnx
src/
  Cvoya.Spring.Core/              # Domain interfaces and types (no infrastructure deps)
  Cvoya.Spring.Dapr/              # Dapr implementations: actors, dispatch, McpServer, EF data
  Cvoya.Spring.A2A/               # A2A protocol client and server
  Cvoya.Spring.AgentRuntimes/     # Per-runtime launchers + the launcher registry
  Cvoya.Spring.AgentSidecar/      # TypeScript A2A bridge bundled into agent images
  Cvoya.Spring.AgentSdk/          # Runtime-image-facing typed messaging client (MCP transport)
  Cvoya.Spring.ModelProviders/    # Per-model-provider wire-format adapters
  Cvoya.Spring.RuntimeCatalog/    # Loads and serves runtime-catalog.yaml
  Cvoya.Spring.Manifest/          # Package / artefact YAML parsing and validation
  Cvoya.Spring.Connectors.Abstractions/   # The IConnectorType plugin contract
  Cvoya.Spring.Connector.GitHub/  # GitHub connector plugin
  Cvoya.Spring.Connector.Arxiv/   # ArXiv connector plugin
  Cvoya.Spring.Connector.WebSearch/       # Web-search connector plugin
  Cvoya.Spring.Dispatcher/        # Container-runtime host process
  Cvoya.Spring.Host.Api/          # spring-api — stateless HTTP front door
  Cvoya.Spring.Host.Worker/       # spring-worker — execution host
  Cvoya.Spring.Cli/               # The "spring" CLI
  Cvoya.Spring.Web/               # The Next.js portal
agents/
  a2a-sidecar/                    # A2A sidecar bridge sources
  spring-voyage-agent/            # The native spring-voyage agent runtime
  spring-voyage-agent-sdk/        # Agent-SDK sources
eng/
  dapr/                           # Dapr component configs (local + production profiles)
  runtime-catalog/                # runtime-catalog.yaml — the AgentRuntime catalogue
  release/  build/  deploy/       # Release, build, and deployment scripts
packages/                         # Domain packages (agents, units, skills, workflows)
tests/                            # unit / integration / e2e / smoke / fixtures
```

See [Components § .NET projects](../architecture/components.md#net-projects) for what
each project owns.

## Key Design Principles

**`Cvoya.Spring.Core` has no infrastructure dependency.** All interfaces and types
are pure .NET — zero Dapr, zero NuGet packages. Dapr-specific implementations live
in `Cvoya.Spring.Dapr`. This makes core logic testable without Dapr infrastructure.

**Two hosts with explicit roles.** `spring-api` is a stateless HTTP front door;
`spring-worker` is *the* execution host — it owns the Dapr actors, A2A dispatch,
the platform MCP server, and EF Core migrations. See
[Components](../architecture/components.md) and
[ADR-0054](../decisions/0054-one-mcp-server-one-execution-host.md).

**A unit is an agent that has children.** Composition is recursive; there is no
separate "orchestrator" concept. The platform delivers messages — it does not
orchestrate. How a unit routes work across its members is runtime behaviour, not
platform configuration ([ADR-0053](../decisions/0053-units-are-agents-and-one-way-delivery.md)).

**Actors are the concurrency boundary.** All mutable runtime state lives inside
Dapr actors. No shared mutable state between actors. State changes happen within
actor turns.

**Domain workflows run in containers.** Never add domain workflows to the host
process. Domain logic deploys as container images, decoupled from platform releases.

**The platform never inspects domain payloads.** Domain messages are one-way
events; delivery decisions are based on `MessageType` and addressing, never on
payload content. Domain semantics live in packages.
