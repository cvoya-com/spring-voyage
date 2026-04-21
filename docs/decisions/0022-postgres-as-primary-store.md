# 0022 — PostgreSQL as primary store; Dapr state store for actor runtime state

- **Status:** Accepted — relational data (tenants, definitions, activity history, secrets, expertise, …) lives in PostgreSQL through EF Core; actor runtime state goes through the Dapr state store abstraction (PostgreSQL today, swappable per-environment).
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Dapr/Data/SpringDbContext.cs`, `src/Cvoya.Spring.Dapr/Data/Configurations/`, `dapr/components/local/statestore.yaml`.
- **Related docs:** [`docs/architecture/infrastructure.md`](../architecture/infrastructure.md), [`docs/architecture/agent-runtimes-and-tenant-scoping.md`](../architecture/agent-runtimes-and-tenant-scoping.md) (tenant-scoping model).

## Context

V2 has two distinct data shapes:

1. **Relational business data** — tenants, agent / unit definitions, conversations, activity events, expertise entries, credential-health rows, secrets metadata, package bindings. This data benefits from joins, schema constraints, indexes, and transactions.
2. **Actor runtime state** — an actor's per-instance fields (active conversation slot, pending queue, mailbox channel cursors, persistent registry rows). This data is private to one actor at a time, accessed turn-by-turn, and ideally portable across backends.

Two extreme positions were on the table: "everything through Dapr state store" (uniform abstraction; loses SQL) or "everything through EF Core" (loses Dapr's actor-state portability and transactional reminder semantics). Both were rejected; the question was where exactly to draw the line.

## Decision

**PostgreSQL is the primary operational database for relational business data, accessed via EF Core (`SpringDbContext` + `IEntityTypeConfiguration<T>` per entity). Actor runtime state goes through the Dapr state store abstraction — backed by PostgreSQL today via the Dapr Postgres component, but swappable to Redis / Cosmos DB / Azure Tables without code changes.**

- **Relational data stays relational.** `tenants`, `agent_definitions`, `units`, `conversations`, `activity_events`, `expertise_entries`, `tenant_agent_runtime_installs`, `tenant_connector_installs`, `credential_health` — every business entity is an EF Core entity with explicit configurations and tenant query filters (see [`docs/architecture/agent-runtimes-and-tenant-scoping.md`](../architecture/agent-runtimes-and-tenant-scoping.md)). EF Core's tracking + transactions + migrations are non-negotiable for this shape.
- **Actor state goes through Dapr.** `AgentActor` / `UnitActor` runtime fields are persisted via `StateManager`. The Dapr state store backend is operationally PostgreSQL today, but the application code never references it directly; swapping to Redis or Cosmos in another deployment is a YAML change.
- **One operational database in OSS.** PostgreSQL serves both roles. Operators run one Postgres instance; the Dapr abstraction is about portability of the application code, not about needing a different database.
- **Migrations are EF Core's.** `DatabaseMigrator` runs EF migrations on Worker host startup ([`docs/developer/operations.md`](../developer/operations.md)). The Dapr state component manages its own schema independently.

## Alternatives considered

- **All-in on Dapr state store.** Loses SQL queries, joins, and constraints for the bulk of the data shape. Tenant-scoping (`HasQueryFilter` + `ITenantContext`) becomes unimplementable; activity-event reads degrade from indexed range scans to per-key fetches.
- **Separate databases per concern.** Adds operational surface (one more thing to back up, monitor, patch, secret-rotate) for no application benefit. The Dapr abstraction already gives us swap-ability when an environment genuinely needs it.
- **Dapr Workflows over EF for activity history.** Workflows are durable but not queryable; activity history is a query-heavy surface (filters, paging, time ranges, source aggregations).

## Consequences

- **One backup story.** Operators back up one PostgreSQL instance to capture both relational data and (today's) actor state.
- **Dapr state-store choice is per-deployment, not per-tenant.** Cloud overlays can swap to Cosmos DB by re-pointing the Dapr component YAML — no application code change.
- **EF Core configuration is the place to enforce tenant scoping.** Every entity that implements `ITenantScopedEntity` carries a query filter; cross-tenant reads must go through `ITenantScopeBypass.BeginBypass(reason)` (see `CONVENTIONS.md` § 13).
- **Migrations live with the Worker host.** Host.Api never runs migrations; this prevents double-bootstrap races.
- **Schema changes are first-class.** Adding a new entity is "add EF entity + configuration + migration"; we never have to think about state-store schema changes for business data.
