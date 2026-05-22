# Tenants and Permissions

Spring Voyage supports multiple organizations on a single platform deployment through **tenants**. Each tenant is an isolated organizational unit with its own users, units, agents, and resources.

## What is a Tenant?

A tenant is the top-level boundary for:

- **Access control** -- users in one tenant cannot see or interact with another tenant's resources
- **Resource isolation** -- each tenant's agents, state, messages, and events are separated
- **Budgeting** -- cost tracking and budget limits apply per tenant
- **Policy** -- tenant-wide defaults govern all units within

A tenant has a stable `Guid` identity and a `display_name`. The tenant row itself anchors the membership graph: top-level units appear as membership rows whose parent is the tenant, and the membership graph rooted there is the addressing fabric for the whole deployment. There is no separate "root unit" entity.

The OSS deployment runs functionally single-tenant. Every fresh-install row is owned by the deterministic v5 UUID `OssTenantIds.Default` (`dd55c4ea-8d72-5e43-a9df-88d07af02b69`); see [Data & identity](../architecture/data-and-identity.md#the-oss-default-tenant).

## `TenantUser`: the authenticated principal

A **`TenantUser`** is the authenticated principal of Spring Voyage scoped to **one tenant** ([ADR-0047 §1](../decisions/0047-platform-user-human-split.md)). It is a distinct actor kind from `Human` — the [`Human`](humans.md) row is a configuration entity declared by a package; the `TenantUser` is the entity that holds an authenticated session, owns display-side connector identities, and answers "who am I, on this connector, in this tenant?"

The natural key on `TenantUserEntity` is `(tenant_id, auth_subject)`, where `auth_subject` is the OAuth `sub` claim (nullable in OSS dev where the operator may not OAuth-authenticate — there the row is pinned by its deterministic UUID below).

### Cross-tenant identity is two rows

A `TenantUser` belongs to exactly one tenant. The **same human authenticated against two tenants produces two distinct `TenantUser` rows**, each with its own connector-identity history. After OAuth login the system looks up every `TenantUser` whose `auth_subject` matches the OAuth `sub`; the caller picks a tenant context; subsequent requests operate in that tenant context. There is no global-user concept and no shared connector-identity history across tenants — a user who appears in two tenants and uses GitHub in both has two `TenantUserConnectorIdentity` rows for GitHub, one per tenant, and may legitimately have different handles configured per tenant.

This is the deliberate counterpart to the cross-tenant isolation rules below ([Multi-Tenancy Isolation](#multi-tenancy-isolation)) — identity rows respect the same tenant boundary as data rows.

### Display-side connector identity lives here

Connector handles — a GitHub login, a Slack member id, an email — are owned by the `TenantUser`, not by the `Human` row. The mapping rows live in `TenantUserConnectorIdentity` with the natural key `(tenant_id, tenant_user_id, connector_id)` and the narrow shape `{ username, display_handle? }` ([ADR-0047 §2](../decisions/0047-platform-user-human-split.md)). The row is strictly display identity — no PAT, no installation override, no auth fields. Outbound credentials live on the unit binding, never on the tenant-user row.

The `Human → TenantUser` mapping is the display / mention / attribution seam — see [Humans § Human → TenantUser display mapping](humans.md#human--tenantuser-display-mapping).

### OSS operator `TenantUser`

The OSS deployment ships with exactly one `TenantUser` — the operator. The id is a deterministic v5 UUID pinned as `OssTenantUserIds.Operator` (`5c4c8e29-d91b-5b50-8651-64536cfb68ee`), derived from namespace `00000000-0000-0000-0000-000000000000` and label `cvoya/tenant-user/oss-operator` — the same recipe shape used by `OssTenantIds.Default` ([ADR-0047 §3](../decisions/0047-platform-user-human-split.md)). The constant is exposed on `src/Cvoya.Spring.Core/Tenancy/OssTenantUserIds.cs` with both dashed and no-dash string literals for grep-ability across configuration files, dashboards, and audit logs. See [Data & identity](../architecture/data-and-identity.md#the-oss-default-tenant).

In OSS every `Human` resolves to this single tenant user — the operator's GitHub / Slack / Linear handles are configured once, on the operator's `TenantUserConnectorIdentity` rows, and serve every `Human` declared by every installed package.

Multi-`TenantUser` OSS sign-in (the umbrella's "OUT1") is **out of scope for v0.1** — the schema and surfaces do not preclude N, but admin and sign-in flows for additional OSS tenant users land later.

## Platform roles

Three platform-role policies gate the API surface:

| Role | Scope |
|------|-------|
| **PlatformOperator** | Platform-wide mutation — tenants, platform config, connector provisioning |
| **TenantOperator** | Tenant configuration — model providers, secrets, budgets, activity settings |
| **TenantUser** | In-product usage — units, agents, threads, messages, the caller's own tokens |

In OSS every authenticated caller is granted all three; the cloud overlay scopes them per identity.

## Unit roles

Within a unit a human holds one of three roles:

| Role | What they can do |
|------|------------------|
| **Owner** | Full control over the unit — configure, manage members, set policies, delete |
| **Operator** | Start/stop agents, interact with agents, view everything |
| **Viewer** | Read-only access — state, activity feed, metrics, agent status |

Permission resolution for a `(human, unit)` pair is **hierarchy-aware**: a direct grant wins, otherwise the nearest ancestor grant cascades down, and a unit can opt out of inheritance by being `Isolated`. The walk is depth-bounded and fails closed. See [Security § Authorisation](../architecture/security.md#authorisation) and [ADR-0013](../decisions/0013-hierarchy-aware-permission-resolution.md).

## Tool authorisation

What an agent may *do* is governed by its effective tool set and unit policy, not by a separate per-agent permission list. Every `sv.*` MCP tool call passes the effective-grant gate and unit-policy enforcement before the tool runs. See [Tools](tools.md) and [Architecture: Security](../architecture/security.md#tool-authorisation).

## Tenant Policies

Tenant-level policies apply defaults to all units unless overridden:

- **Initiative limits** -- maximum initiative level for any agent in the tenant
- **Cost budgets** -- monthly budget with alert thresholds and hard limits
- **Execution limits** -- allowed container runtimes, maximum container count
- **Connector restrictions** -- which connector types are available
- **Security** -- MFA requirements, session timeouts

## Authentication

### CLI Authentication

Users authenticate via the `spring auth` command, which opens the web portal for login (Google OAuth or other identity providers). New users create an account with minimal profile information and terms acceptance.

All subsequent CLI commands use the stored credential. The CLI rejects commands if the user is not authenticated.

### API Tokens

For non-interactive use (CI/CD, scripts), users can generate long-lived API tokens via the web portal or CLI. Tokens are named, scoped, and can be listed and revoked by the user or by a tenant admin.

### Local Development Exception

When the platform runs in local development mode (`--local`), authentication is disabled. All commands execute as an implicit local user. This mode is for development and testing only.

## Multi-Tenancy Isolation

Tenants are isolated at multiple levels:

- **Runtime** -- each tenant maps to a separate namespace. Pub/sub, state stores, and actor identities are namespace-scoped.
- **Data** -- all tenant data in the database is scoped by tenant ID, enforced at the repository layer.
- **Resources** -- per-tenant resource quotas (CPU, memory, storage, container count) in production deployments.

The combination ensures no data leakage between tenants at either the application or infrastructure level.
