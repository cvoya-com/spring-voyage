# Infrastructure: Dapr

Spring Voyage V2 is built on [Dapr](https://dapr.io) -- a distributed application runtime that provides infrastructure building blocks as a sidecar process. Dapr is the reason the platform can be language-agnostic, infrastructure-agnostic, and deployable from a laptop to Kubernetes without code changes.

## Why Dapr

Dapr solves the distributed systems problems that Spring Voyage needs solved:

- **State management** without coupling to a specific database
- **Pub/sub messaging** without coupling to a specific broker
- **Service invocation** with automatic mTLS, retries, and observability
- **Virtual actors** for the agent concurrency model
- **Durable workflows** for orchestration
- **Secret management** without coupling to a specific vault
- **Input/output bindings** for external system integration

All of these are provided as HTTP/gRPC APIs on `localhost:3500`. The application talks to the sidecar; the sidecar talks to the infrastructure.

## The Sidecar Pattern

Every process in the system -- the API host, worker host, execution environment containers, workflow containers -- runs alongside a Dapr sidecar. The sidecar:

- Intercepts outbound calls and adds mTLS, retries, and tracing
- Provides the building block APIs (state, pub/sub, actors, etc.)
- Manages component connections (database, message broker, secret store)
- Emits distributed traces automatically

The application never connects directly to PostgreSQL for state, Redis for pub/sub, or Key Vault for secrets. It calls the Dapr sidecar, and the sidecar connects to whatever backend is configured.

## Building Blocks Used

| Building Block | What It Provides | Spring Voyage Usage |
|---------------|------------------|-------------------|
| **Actors** | Virtual actors with turn-based concurrency, reminders, timers | Agent, Unit, Connector, Human actors |
| **Workflows** | Durable orchestration with task chaining, fan-out, parallel execution | Platform-internal lifecycle workflows |
| **Pub/Sub** | Pluggable pub/sub with topic-based routing | Event distribution, activity streams, agent-to-agent observation |
| **State Management** | Pluggable state stores with consistency guarantees | Agent runtime state (active conversation, pending queue, observations) |
| **Bindings** | Input/output connectors to external systems | Webhook reception, cron schedules, external event ingestion |
| **Secrets** | Pluggable secret stores | API keys, webhook secrets, connector credentials |
| **Service Invocation** | Secure service-to-service calls | Inter-host communication, sidecar-to-sidecar calls |
| **Configuration** | Dynamic configuration with change subscriptions | Feature flags, policy overrides, model selection |

## Language-Agnostic Architecture

Because Dapr exposes everything as HTTP/gRPC on localhost, any process that can make HTTP calls is a first-class participant. This enables:

- **.NET (C#)** for the infrastructure layer -- actors, routing, workflows, API surface
- **Python** for agents that need direct LLM SDK integration or AI frameworks (Google ADK, LangGraph)
- **Any other language** for agent brains that can speak HTTP/gRPC

The infrastructure layer is fixed in .NET. The agent brain logic is a free choice.

## Pluggable Backends

Every Dapr building block has multiple backend implementations, swapped via YAML configuration:

| Building Block | Development | Production |
|---------------|------------|------------|
| State Store | PostgreSQL | PostgreSQL, Redis, Cosmos DB |
| Pub/Sub | Redis | Kafka, Azure Event Hubs |
| Secrets | Local file | Azure Key Vault, HashiCorp Vault, Kubernetes Secrets |
| Configuration | PostgreSQL | PostgreSQL, Redis |

Switching from development to production backends requires changing YAML component files -- no code changes.

## Resilience

Dapr provides pluggable resiliency policies configured per building block via YAML:

- **Retries** with exponential backoff for transient failures
- **Timeouts** to prevent hanging calls
- **Circuit breakers** to prevent cascading failures when a backend is down

These are configured declaratively and apply automatically to all calls through the sidecar.
