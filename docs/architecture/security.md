# Security

> **[Architecture index](README.md)** · Related: [Units & agents](units-and-agents.md), [Data & identity](data-and-identity.md), [Connectors](connectors.md)
>
> Multi-tenancy, OAuth/SSO, and platform operations are commercial extensions in
> the private repository. This page covers the OSS security model.

How Spring Voyage authenticates callers, authorises actions, isolates tenants,
and stores secrets.

---

## Authentication

A caller authenticates to the API with an **API token** — a hashed row in
`api_tokens` with an optional scope set and per-token expiry. `spring auth`
negotiates a token through the portal; the CLI stores it locally and sends it on
every call. Tokens can be listed and revoked; a revoked token is rejected
immediately.

**Local-dev mode** (`--local`) bypasses authentication — every call runs as an
implicit local user. It is for development and testing only.

Dapr provides mTLS for all service-to-service traffic.

## Authorisation

### Platform roles

Three platform-role policies gate the API surface:

| Role | Scope |
|------|-------|
| `PlatformOperator` | Platform-wide mutation — tenants, platform config, connector provisioning |
| `TenantOperator` | Tenant configuration — model providers, secrets, budgets, activity settings |
| `TenantUser` | In-product usage — units, agents, threads, messages, the caller's own tokens |

In OSS every authenticated caller is granted all three (`OssAllRolesClaimSource`);
the cloud overlay scopes them per identity via its own role-claim source.
Endpoints declare the role they require; many endpoint groups self-gate
internally.

### Unit roles and hierarchy-aware resolution

Within a unit a human holds **Owner**, **Operator**, or **Viewer**. Permission
resolution for a `(human, unit)` pair is **hierarchy-aware**: a grant on an
ancestor unit cascades to descendants ([ADR-0013](../decisions/0013-hierarchy-aware-permission-resolution.md)):

1. **A direct grant wins** — including a deliberate downgrade.
2. **Otherwise the nearest ancestor grant wins** — depth never amplifies a
   permission.
3. **Isolation stops inheritance** — a unit's `UnitPermissionInheritance` flag
   (`Inherit` by default, `Isolated` to opt out) is the permission-layer
   analogue of an opaque boundary.
4. **Fail closed** — an unreadable inheritance flag is treated as `Isolated`.
5. **Depth-bounded** to 64 hops.

`ResolveEffectivePermissionAsync` (the walking resolver) authorises real
requests; `ResolvePermissionAsync` (direct-grant only) backs editor and audit
surfaces.

### Hat ↔ unit reachability gate

A second, **orthogonal** gate applies when a *person* (a tenant user, via the
Web API or CLI — never an agent-to-agent send) messages a unit or agent. A
person sends **as a Hat** (a human member of a unit), and a Hat reaches only the
unit it is a direct member of plus that unit's direct members (agents,
sub-units, co-member humans) — not the unit's parent, not into a sibling
sub-unit. The message-send endpoints compute the wearable-Hat set
(`IHatReachabilityService`) for the recipient and reject the send when it is
empty (`403 NoReachableHat`) or when an explicit `--as` Hat cannot reach the
target (`403 HatCannotReachTarget`); otherwise the reaching Hat is stamped as
`Message.From`. This is independent of the role-grant resolution above — in OSS
the operator is implicit-Owner everywhere, so reachability is the meaningful
membership gate. See [ADR-0062 § 11](../decisions/0062-tenant-user-human-explicit-binding.md)
and [Humans](../concepts/humans.md#reaching-units-and-agents-the-hat--unit-gate).

### Tool authorisation

Every `sv.*` MCP tool call passes two gates inside the `McpServer` before the
tool runs: the **effective-grant gate** (does this subject have the tool
granted?) and **unit-policy enforcement** (does any unit the agent belongs to
deny it?). A unit policy can deny any tool. See [Units & agents](units-and-agents.md).

## Tenant isolation

The OSS core has no `TenantId`-as-infrastructure — tenancy is a `tenant_id`
value on every `ITenantScopedEntity`, and `SpringDbContext` enforces it with a
global query filter resolved against the injected `ITenantContext`. Cross-tenant
reads/writes require an explicit, audited `ITenantScopeBypass` scope. See
[Data & identity](data-and-identity.md). The cloud overlay layers tenant-scoped
middleware and repositories on top via DI.

## Configuration tiers

Sensitive material lives in one of three tiers so each can be rotated, audited,
and scoped independently:

| Tier | Surface | Examples | Owner |
|------|---------|----------|-------|
| **1 — platform-deploy** | `IConfiguration` / env / `spring.env` | DB connection string, Dapr wiring, the GitHub App keypair (the *instance's* identity) | Ops, at deploy time |
| **2 — tenant-default** | `SecretScope.Tenant` registry rows | LLM provider credentials, tenant-wide tokens | Tenant operator, post-deploy |
| **3 — unit-override** | `SecretScope.Unit` registry rows | Per-unit variants of any tier-2 credential | Unit operator |

LLM credentials are tier-2/3, **not** tier-1: they describe a workload, vary per
tenant and per unit, and must rotate without a restart. The GitHub App keypair
is tier-1: it identifies the deployment itself.

## The secrets stack

Three layers plus an access-policy seam, all defined in `Cvoya.Spring.Core/Secrets/`
so the cloud overlay can substitute any one (e.g. route to Azure Key Vault)
without touching call sites:

| Layer | Default | Responsibility |
|-------|---------|----------------|
| `ISecretStore` | `DaprStateBackedSecretStore` | Opaque plaintext K/V — write returns a `storeKey`, read returns plaintext |
| `ISecretRegistry` | `EfSecretRegistry` | Metadata — maps `SecretRef(scope, owner, name, version)` to a pointer |
| `ISecretResolver` | `ComposedSecretResolver` | The **only** server-side plaintext-read surface; composes policy + registry + store |
| `ISecretAccessPolicy` | `AllowAllSecretAccessPolicy` (OSS) | Per-scope authorisation; the cloud overlay substitutes a role-aware policy |

- **At-rest encryption.** The store wraps every value in an AES-GCM-256 envelope
  before it touches Dapr; the associated data is `{tenantId}:{storeKey}`, so a
  ciphertext cannot be transplanted across tenants or keys. The key comes from
  `SPRING_SECRETS_AES_KEY` or a key file; the encryptor refuses to start without
  one.
- **HTTP endpoints never return plaintext.** They accept it on `POST`/`PUT` and
  never echo it; the only plaintext path out is `ISecretResolver.ResolveAsync`,
  reached server-side at dispatch time.
- **Versioned rotation.** Registry rows are per-version. `RotateAsync` appends a
  new version and retains old ones so a pinned caller still resolves; `PruneAsync`
  trims history. An `ExternalReference`-origin pointer is never mutated by a
  rotate/delete — that would destroy customer-owned material.
- **Unit → Tenant inheritance.** `ComposedSecretResolver` falls through from a
  unit-scoped miss to the tenant scope ([ADR-0003](../decisions/0003-secret-inheritance-unit-to-tenant.md)),
  re-checking the access policy at *both* scopes — no privilege escalation via
  inheritance. There is no per-agent scope ([ADR-0004](../decisions/0004-per-agent-secrets.md)).
- **Audit.** `ISecretResolver` and `ISecretRegistry` are `TryAdd`-registered, so
  the cloud overlay wraps them with audit/RBAC decorators — which must never log
  plaintext.

## Resilience

Dapr supplies pluggable retry, timeout, and circuit-breaker policies per
building block via YAML. Virtual actors reactivate automatically on failure with
state recovered from the state store; pub/sub is at-least-once with dead-letter
topics.

## Extension points

The OSS platform is a framework. The commercial overlay adds multi-tenancy,
OAuth/SSO, platform operations, cross-tenant federation, and billing by
registering its own implementations of `Cvoya.Spring.Core` interfaces *before*
the OSS `TryAdd*` defaults. See `AGENTS.md` for the extensibility rules.
