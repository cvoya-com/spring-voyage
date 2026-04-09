# Data Persistence

This document describes how Spring Voyage V2 stores data: what goes where, why, and through which abstraction layer.

## Primary Data Store: PostgreSQL

PostgreSQL is the primary relational store, carried forward from v1. It stores:

- Tenant, user, and organizational data
- Agent definitions, unit configurations, and package manifests
- Activity event history
- Audit logs

PostgreSQL is accessed in two ways: directly via Entity Framework Core (for relational data) and through Dapr (for actor runtime state).

## Data Storage Map

| Data | Store | Access Layer |
|------|-------|-------------|
| Tenant/User/Org | PostgreSQL | Direct (EF Core) |
| Agent/Unit definitions | PostgreSQL | Direct (EF Core) |
| Agent runtime state | PostgreSQL (via Dapr) | Dapr State Store |
| Activity events | PostgreSQL | Direct + Pub/Sub |
| Dynamic configuration | PostgreSQL (via Dapr) | Dapr Configuration |
| Secrets (API keys, tokens) | Key Vault / local file | Dapr Secrets |
| Execution artifacts | Object storage (S3/Blob/local) | Dapr Bindings |

## Dapr Abstraction Layers

### State Store

Agent runtime state -- the active conversation, pending conversations, observations, initiative state -- uses the Dapr state store abstraction. The backend is PostgreSQL, but the abstraction allows swapping to Redis, Cosmos DB, or other stores without code changes.

State is persisted automatically by the actor framework. If an actor crashes and reactivates, it resumes from its last persisted state.

### Configuration

Dynamic configuration -- feature flags, policy overrides, model selection -- uses the Dapr Configuration building block. Agents and units subscribe to configuration changes and react in real-time. When a policy changes, all affected actors receive the update without restart.

### Secrets

API keys, webhook secrets, and connector credentials use the Dapr Secrets building block:

- **Development** -- local file (plain JSON, not committed to source control)
- **Production** -- Azure Key Vault, HashiCorp Vault, or Kubernetes Secrets

The application code is identical regardless of which secret backend is configured.

### Pub/Sub

Event distribution uses the Dapr Pub/Sub building block:

- **Development** -- Redis
- **Production** -- Kafka, Azure Event Hubs

Topics are namespaced by unit. Dead letter support handles messages that repeatedly fail processing.

## Schema Management

Database schema is managed via EF Core migrations:

- Applied automatically on startup or via `spring-admin migrate`
- Backwards-compatible within a major version (additive columns, new tables)
- Destructive changes only on major version bumps

Actor state uses versioned serialization. Each actor state type carries a schema version. On activation, the actor detects the stored version and applies migration chains. Migrations can be lazy (on first access) or eager (via `spring-admin migrate actors`).

## Tenant Data Isolation

All tenant data is scoped:

- **PostgreSQL** -- tenant-scoped queries enforced at the repository layer (row-level or schema-per-tenant)
- **Dapr runtime** -- namespace isolation for actors, pub/sub consumer groups, and state store key prefixes

The combination ensures no data leakage between tenants at either the application or infrastructure level.
