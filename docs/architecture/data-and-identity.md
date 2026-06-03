# Data & identity

> **[Architecture index](README.md)** · Related: [Components](components.md), [Units & agents](units-and-agents.md), [Messaging](messaging.md), [Security](security.md)

How Spring Voyage identifies things and where it stores them: the single-identity
model, the wire forms, the split between PostgreSQL and Dapr actor state, and the
OSS default tenant.

The durable identity decision is
[ADR-0036](../decisions/0036-single-identity-model.md); the state-ownership
matrix is [ADR-0040](../decisions/0040-actor-state-ownership-matrix.md).

---

## Identity is a `Guid`

Every actor — unit, agent, human, connector, tenant, tenant-user — has exactly
one stable identifier: a `Guid`. It is the primary key, the foreign-key target,
the activity-log source, the wire-form identity, and the manifest cross-reference
token. It does not change for the actor's lifetime.

There is **no** parallel string identifier — no slug, no namespace+name pair, no
scoped handle. A `display_name` exists for human-facing rendering only: not
unique, not addressable, not a foreign-key target. A `display_name` that parses
as a `Guid` is rejected, so a Guid-shaped token is unambiguously identity.

## Wire forms

| Surface | Form | Example |
|---------|------|---------|
| URL paths, address strings, manifest refs, CLI output, logs | 32-char lowercase no-dash hex | `8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` |
| JSON DTO bodies | dashed `8-4-4-4-12` | `8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7` |

The rule is **emit one form, parse many**: emit is strict per surface; parse is
lenient everywhere (`Guid.TryParse` accepts no-dash, dashed, and braced). JSON
bodies use the dashed form because Kiota and STJ read it natively; everything
else uses no-dash because it is compact and never confused with a name.

### Addresses

An `Address` is `(Scheme, Guid)` — scheme is `agent`, `unit`, `human`, or
`connector` — with canonical wire form `scheme:<32-hex>`:

```
agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
unit:dd55c4ea8d725e43a9df88d07af02b69
```

There is no path or `scheme://` URI form. An address identifies an actor; it
does not encode hierarchy. Actors have **flat** Dapr ids derived from the
`Guid`; the directory resolves an address to an actor in a single lookup, and
permission-aware traversal of the membership graph happens at resolution time
inside the directory ([ADR-0023](../decisions/0023-flat-actor-ids.md)).

### Manifests

Inside one manifest file, references are **local symbols** — the install
pipeline mints a fresh `Guid` per artefact and binds the symbol to it. Across
packages, references are `Guid`s. Display-name lookup across the catalogue does
not exist — names are not unique.

## Where data lives

Spring Voyage stores data in two places, with a sharp ownership rule.

| Store | Holds |
|-------|-------|
| **PostgreSQL** (EF Core, `SpringDbContext`) | Configuration, authorization, and business data — definitions, live config, the membership graph, threads and messages, activity events, costs, secrets registry, tenants and tenant-users |
| **Dapr state store** (PostgreSQL-backed, swappable) | Runtime-ephemeral actor scratch — per-thread mailbox channels, the observation channel, lifecycle status, initiative state, per-thread read cursors, the thread hop count |

[ADR-0040](../decisions/0040-actor-state-ownership-matrix.md) is the
state-ownership matrix: every piece of actor state has a single home, classified
as runtime-ephemeral scratch (stays in Dapr state), configuration / authorization
/ business data (lives in EF, tenant-scoped), or computable from EF (not stored
at all). This removed the dual-storage hazards where a value lived in both
stores and could disagree — the membership graph, ACLs, and entity metadata are
**EF-authoritative**, with no actor-state mirror.

EF business entities span definitions (`agent_definitions`, `unit_definitions`),
live config (`agent_live_config`, `unit_live_config`), the membership graph
(`unit_memberships`, `unit_subunit_memberships`), permissions
(`unit_human_permissions`), connector bindings (`unit_connector_bindings`),
threads and messages, activity events, cost records, budget limits, cloning
policies, the secret registry, tenants, tenant-users, and tenant-user connector
identities.

## Tenancy

The OSS core models tenancy as a **value** — a `tenant_id` column on every
`ITenantScopedEntity` — not as infrastructure. `SpringDbContext` applies a global
query filter (`TenantId == ITenantContext.CurrentTenantId && DeletedAt == null`)
to every business entity, and auto-populates `TenantId` on write from the
injected `ITenantContext`. Code never hardcodes a tenant; cross-tenant access
goes through an explicit `ITenantScopeBypass.BeginBypass(reason)` scope.

The cloud overlay swaps in a scoped `ITenantContext` and tenant-aware
repositories via DI. OSS code must not assume a single tenant or hardcode the
default tenant id.

### The OSS default tenant

The OSS deployment ships functionally single-tenant. Every tenant-scoped row in
a fresh install is owned by `OssTenantIds.Default` — a deterministic v5 UUID
(`dd55c4ea-8d72-5e43-a9df-88d07af02b69`), derived from a fixed namespace and the
label `cvoya/tenant/oss-default` and pinned as a literal. A v5 UUID is
recomputable from outside the platform, self-documenting, and collision-free
against random Guids. The single OSS operator `TenantUser` is pinned the same way
as `OssTenantUserIds.Operator`.

`Guid.Empty` is reserved for "uninitialised / programmer error" — never a real
tenant id.

A clean OSS install seeds the operator `TenantUser` but **no** `Human` rows.
Humans ("Hats") are created only as unit members; each is auto-associated with
the operator (`humans.tenant_user_id`) at mint time, and deleting a unit
removes its human memberships and garbage-collects any Hat left with no
membership (#2972, [ADR-0062 § 11](../decisions/0062-tenant-user-human-explicit-binding.md)).
Which Hats a tenant user may wear to message a given unit/agent is decided by
`IHatReachabilityService` — see [Security § Hat ↔ unit reachability gate](security.md#hat--unit-reachability-gate).

## Connector-native identity

Internally the platform is single-identity (`Guid`); the outside world addresses
humans through connector-native identifiers (a GitHub login, a Slack handle).
The bridge is `TenantUserConnectorIdentity`, keyed
`(tenant_id, tenant_user_id, connector_id)` — a strictly display-only row
(`username`, optional `display_handle`), no auth fields. An `sv.*` tool that must
act on a GitHub user resolves `Human → TenantUser → TenantUserConnectorIdentity`;
an inbound webhook resolves the other way. Outbound credentials are **not** here
— they live on the unit's connector binding (see [Connectors](connectors.md)).
The same connector login may legitimately appear on two `TenantUser` rows in two
different tenants — cross-tenant identity is two rows.
