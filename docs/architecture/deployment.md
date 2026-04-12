# Deployment

> **[Architecture Index](README.md)** | Related: [Infrastructure](infrastructure.md), [CLI & Web](cli-and-web.md), [Units & Agents](units.md)

---

## Execution Modes

The agent actor (brain) and execution environment (hands) are separate — see [Units & Agents](units.md) for the container-based execution pattern and [Messaging](messaging.md) for async dispatch, cancellation, and streaming details. This section covers the isolation modes available for execution environments.


| Mode                  | Isolation   | Startup | Best For                               |
| --------------------- | ----------- | ------- | -------------------------------------- |
| `in-process`          | None        | Instant | LLM-only agents, research, advisory    |
| `container-per-agent` | Full        | Seconds | Software engineering, tool use         |
| `ephemeral`           | Maximum     | Seconds | Untrusted code, compliance             |
| `pool`                | Full (warm) | Instant | Large-scale, mixed workloads           |
| `a2a`                 | External    | Varies  | External agents (ADK, LangGraph, etc.) |


---

## Solution Structure

The solution follows a layered architecture with clean separation between domain abstractions and infrastructure:

- **`Cvoya.Spring.Core`** — Domain interfaces and types. No Dapr or infrastructure dependencies. Defines `IAddressable`, `IMessageReceiver`, `IOrchestrationStrategy`, `IActivityObservable`, and all domain models.
- **`Cvoya.Spring.Dapr`** — Dapr implementations: actors (`AgentActor`, `UnitActor`, `ConnectorActor`, `HumanActor`), orchestration strategies, state management, and routing.
- **`Cvoya.Spring.A2A`** — A2A protocol client and server for cross-framework agent communication.
- **`Cvoya.Spring.Connector.GitHub`** — GitHub connector with webhook handling and skills.
- **`Cvoya.Spring.Host.Api`** — ASP.NET Core API host (REST, WebSocket, SSE, auth, local dev mode).
- **`Cvoya.Spring.Host.Worker`** — Headless worker host for Dapr actors and workflows.
- **`Cvoya.Spring.Cli`** — The `spring` command-line tool.
- **`Cvoya.Spring.Web`** — Next.js/React web dashboard.
- **`packages/`** — Domain packages with agent/unit definitions, skills, workflow containers, and execution environments.
- **`dapr/`** — Dapr component configuration (pub/sub, state, bindings, secrets, resiliency).
