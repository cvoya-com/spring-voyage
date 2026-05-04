# Tenants and Permissions

Spring Voyage supports multiple organizations on a single platform deployment through **tenants**. Each tenant is an isolated organizational unit with its own users, units, agents, and resources.

## What is a Tenant?

A tenant is the top-level boundary for:

- **Access control** -- users in one tenant cannot see or interact with another tenant's resources
- **Resource isolation** -- each tenant's agents, state, messages, and events are separated
- **Budgeting** -- cost tracking and budget limits apply per tenant
- **Policy** -- tenant-wide defaults govern all units within

A tenant has a stable `Guid` identity and a `display_name`. The tenant row itself anchors the membership graph: top-level units appear as membership rows whose parent is the tenant, and the membership graph rooted there is the addressing fabric for the whole deployment. There is no separate "root unit" entity.

The OSS deployment runs functionally single-tenant. Every fresh-install row is owned by the deterministic v5 UUID `OssTenantIds.Default` (`dd55c4ea-8d72-5e43-a9df-88d07af02b69`); see [Identifiers § 5](../architecture/identifiers.md#5-the-oss-default-tenant-id).

## User Roles

### System-Level Roles

| Role | What They Can Do |
|------|-----------------|
| **Platform Admin** | Create and delete tenants, manage users across tenants, configure platform-wide settings |
| **User** | Create units within their tenant, join units they're invited to |

### Tenant-Level Roles

| Role | What They Can Do |
|------|-----------------|
| **Tenant Admin** | Full control within the tenant -- manage users, policies, budgets, all units |
| **Unit Creator** | Create and manage their own units. Cannot see other users' units unless invited. |
| **Member** | Participate in units they're invited to. Cannot create new units. |

### Unit-Level Roles

| Role | What They Can Do |
|------|-----------------|
| **Owner** | Full control over the unit -- configure, manage members, set policies, delete |
| **Operator** | Start/stop agents, interact with agents, approve workflow steps, view everything |
| **Viewer** | Read-only access -- state, activity feed, metrics, agent status |

Permission inheritance in nested units is opt-in. Each unit manages its own access control list. A unit can choose to inherit permissions from its parent.

## Agent Permissions

Agents also have scoped access within the platform:

| Permission | Description |
|------------|-------------|
| **message.send** | Send messages to specified addresses or roles |
| **directory.query** | Query the unit, parent, or root directory |
| **topic.publish / topic.subscribe** | Publish to or subscribe to pub/sub topics |
| **observe** | Subscribe to another agent's activity stream |
| **workflow.participate** | Be invoked as a step in a workflow |
| **agent.spawn** | Create new agents at runtime (future capability) |

Higher initiative levels implicitly grant more permissions. A proactive agent gains `reminder.modify` to adjust its own schedule. An autonomous agent additionally gains `topic.subscribe` and `activation.modify`.

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
