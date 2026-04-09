# Security and Resilience

This document covers authentication, authorization, network security, multi-tenancy isolation, and failure recovery in Spring Voyage V2.

## Authentication

### User Authentication

Users authenticate via the `spring auth` command, which opens the web portal in the browser. The portal handles:

1. Login via identity providers (Google OAuth, etc.)
2. Account creation for new users (minimal profile, terms acceptance)
3. Issuing a session credential back to the CLI

All subsequent CLI commands use the stored credential. Unauthenticated commands (except `spring auth`) are rejected.

### API Tokens

For non-interactive use (CI/CD, scripts), authenticated users generate long-lived API tokens:

- Generated via the web portal or CLI
- Named, scoped, and trackable (creation time, last used)
- Listable and revocable by the owning user
- Listable and revocable by tenant admins (including bulk invalidation)
- Rejected immediately upon invalidation

### Local Development

When the API Host runs in local development mode (`--local`), authentication is disabled. All commands execute as an implicit local user.

## Network Security

### Dapr-Native mTLS

All service-to-service communication uses mutual TLS, managed by Dapr:

- Every sidecar has a certificate
- All inter-sidecar calls are encrypted and authenticated
- Certificate rotation is automatic

No application code manages TLS. The sidecar handles it.

### Access Control Policies

Dapr access control policies restrict which actors can access which building blocks. An agent actor can access its own state store keys but not another agent's. These are configured declaratively in YAML.

## Authorization

### Permission Model

Authorization operates at three levels:

**System level:**
- Platform Admin -- manage tenants, users, system config
- User -- create units, join invited units

**Tenant level:**
- Tenant Admin -- full control within the tenant
- Unit Creator -- create and manage own units
- Member -- participate in invited units

**Unit level:**
- Owner -- full control over the unit
- Operator -- interact with agents, approve workflow steps
- Viewer -- read-only access

### Agent Permissions

Agents have scoped access:
- `message.send` -- send to specified addresses/roles
- `directory.query` -- query unit/parent/root directory
- `topic.publish` / `topic.subscribe` -- pub/sub access
- `observe` -- subscribe to another agent's activity stream
- `workflow.participate` -- be invoked as a workflow step

Higher initiative levels implicitly grant additional self-modification permissions.

### Boundary Enforcement

Permission checks happen at address resolution time. When the directory resolves a path address, it evaluates each boundary along the path, checks the sender's permissions, and either returns the actor ID or rejects the message. This is one synchronous check.

## Multi-Tenancy Isolation

Tenants are isolated at multiple layers:

| Layer | Mechanism |
|-------|-----------|
| **Runtime** | Dapr namespaces -- actors, pub/sub consumer groups, state store key prefixes |
| **Data** | Tenant-scoped queries enforced at the repository layer |
| **Resources** | Per-tenant resource quotas (CPU, memory, storage, containers) via Kubernetes |
| **Secrets** | Namespaced secret stores |

## Resilience

### LLM API Failures

- Retry with exponential backoff
- Circuit breaker prevents cascading failures when a provider is down
- Agent falls back to queuing work

### Execution Environment Crashes

- Actor detects failure via heartbeat/timeout
- Conversation marked as failed
- Work can be resumed from last checkpoint or re-queued
- Escalation to human if recovery fails

### Actor Failures

- Dapr virtual actors are automatically reactivated on failure
- State is persisted in the state store -- recovery is transparent
- No manual intervention needed

### Pub/Sub Delivery

- At-least-once delivery guarantees
- Dead letter topics for messages that repeatedly fail processing
- Message deduplication via unique message IDs

### Execution Environment Security

- Sandboxed by default: no network, no filesystem beyond workspace
- Explicit permission grants for network, filesystem, and secrets
- Container isolation via Podman/Docker
