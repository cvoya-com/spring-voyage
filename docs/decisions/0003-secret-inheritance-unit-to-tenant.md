# 0003 — Secret inheritance semantics (Unit → Tenant)

- **Status:** Accepted — automatic Unit → Tenant fall-through at resolve time, gated by a turn-off flag.
- **Date:** 2026-04-13
- **Closes:** [#204](https://github.com/savasp/spring-voyage/issues/204)
- **Related code:** `src/Cvoya.Spring.Dapr/Secrets/ComposedSecretResolver.cs`, `src/Cvoya.Spring.Core/Secrets/ISecretResolver.cs`, `src/Cvoya.Spring.Core/Secrets/SecretResolution.cs`, `src/Cvoya.Spring.Core/Secrets/SecretResolvePath.cs`, `src/Cvoya.Spring.Core/Secrets/ISecretAccessPolicy.cs`, `src/Cvoya.Spring.Dapr/Tenancy/SecretsOptions.cs`

## Context

The unit-scoped secrets work (#122) landed with `SecretScope.Unit` and `SecretScope.Tenant` as strictly-separate registry partitions: `ISecretResolver.ResolveAsync(Unit, unit, name)` returns `null` when no unit-scoped entry exists, even if a tenant-scoped entry with the same name does. Early feedback on the contract (#204) was that customers expect a shared CI token, a tenant-wide observability key, or a shared external-service credential to be visible from unit context without per-unit duplication — and that forcing the user to copy every tenant secret into every unit is paperwork, not security.

Three options were on the table when the issue was filed:

1. **Explicit include.** A unit-level `InheritsFromTenant: string[]` list. Auditable (every inherited name is pinned), but high-paperwork for the common case and adds a new CRUD surface the endpoints have to manage.
2. **Automatic fall-through.** `ResolveAsync(Unit, u, name)` falls back to `(Tenant, tenantId, name)` on miss. Matches how most managed secret systems handle hierarchy (Azure Key Vault + role scoping, AWS Secrets Manager path inheritance, 1Password vault-then-account resolution).
3. **Explicit alias scope.** A new `SecretScope.TenantAlias` value that the resolver transparently re-targets. Leaks an implementation detail (inheritance strategy) into a domain enum that should model ownership, not resolution.

The issue also flagged two non-negotiable correctness properties that any option must satisfy:

- **No cross-tenant leaks.** The registry already enforces tenant isolation via `ITenantContext`; the fall-through must not open a new path around that guard.
- **No privilege escalation via inheritance.** A unit-level `Read` grant alone must not produce tenant-scoped plaintext.

And one operational property:

- **Turn-off knob for strict-isolation customers.** Some deployments will want unit/tenant to stay strictly separate; the default must not foreclose that.

## Decision

**Adopt automatic Unit → Tenant fall-through at resolve time (option 2)** with the following contract, implemented in `ComposedSecretResolver.ResolveWithPathAsync`:

1. **Resolve-time only, registry-static.** The fall-through is computed in the resolver. No entry ever moves between partitions; `ISecretRegistry.ListAsync(Unit, u)` still returns only unit-owned entries. The metadata surface — CRUD endpoints, list responses — is unchanged.

2. **Order is strict Unit → Tenant.** The unit entry wins when both exist. Only a unit miss triggers the tenant lookup.

3. **Access-policy evaluated at BOTH scopes.** Before the registry is touched at a given scope, the resolver calls `ISecretAccessPolicy.IsAuthorizedAsync(Read, scope, ownerId, ct)` for that scope. A denial fails closed — the resolver returns `SecretResolvePath.NotFound`, never a silently-masked tenant plaintext. This was the security-regression worry from the issue and is now the explicit enforcement point. A new `SecretAccessAction.Read` value was added to the enum to give the policy a distinct signal from CRUD operations.

4. **Tenant → Platform fall-through is NOT included.** Platform-scoped secrets are for infra-owned keys (signing keys for system topics, platform-wide webhook shared secrets) that units have no business observing. Adding fall-through there would muddle the Platform scope's role as an admin-only boundary and create a subtle path for a compromised unit to probe platform keys by name. If a platform key needs to be visible to tenants, the tenant admin publishes it under the tenant scope explicitly.

5. **Opt-out knob.** `Secrets:InheritTenantFromUnit` (bound on `SecretsOptions`) defaults to `true`. Customers who want strict scope isolation set it to `false` and the fall-through path is never taken, not even the tenant-scope access-policy probe.

6. **Path signal for audit.** The resolver's detailed result (`SecretResolution`) exposes a `SecretResolvePath` — `Direct`, `InheritedFromTenant`, or `NotFound` — plus the effective `SecretRef` that was read. This is the hook the audit-log decorator (tracked by #202) consumes so its records include "resolved via inheritance from tenant" without having to mirror the resolver's internal state. The existing `ResolveAsync(SecretRef, ct)` convenience method is preserved and delegates to `ResolveWithPathAsync`.

### Why option 2, not 1 or 3

- **Option 1 (explicit include)** is strictly more auditable, but the cost is that every shared secret requires a per-unit inheritance-list edit. For a platform where units proliferate (clone-per-task, agent-per-conversation), this turns every tenant-level secret rotation into a fan-out CRUD operation. The audit-log decorator (#202) gets us the audit trail without the paperwork: every inherited resolve is recorded with path `InheritedFromTenant` so retroactive questions ("which units read this tenant key?") are answered by log queries, not by registry denormalisation.
- **Option 3 (alias scope)** mingles "who owns this?" with "how is it resolved?" in the same enum. Worse, it requires CRUD endpoints to either reject the alias scope or make sense of storing under an alias — both are awkward. The resolver is the right place for a resolution-strategy concern; the `SecretScope` enum should stay shaped by ownership.

## Consequences

What callers get:

- `ISecretResolver.ResolveAsync(new SecretRef(Unit, u, "token"), ct)` transparently returns the tenant's `"token"` if the unit has no entry and the caller has both unit-scope and tenant-scope `Read` authorization.
- Audit decorators see every resolve with a `SecretResolvePath` so "inherited" lookups are first-class events, not reconstructed by comparing the requested `SecretRef` against a list response.
- Customers who need strict per-scope isolation set `Secrets:InheritTenantFromUnit = false`.

What the private cloud repo gets:

- The new `SecretAccessAction.Read` member is the single authorization hook for resolve-time checks. The cloud's real RBAC implementation — tenant-admin / per-role grants — plugs in via the existing DI override without any call-site change.
- Because the `ISecretResolver` method surface is the new `ResolveWithPathAsync` plus the existing `ResolveAsync`, decorator implementations can layer audit-logging and rotation tracking without extra interfaces.

What operators give up vs. option 1:

- A unit caller can observe a tenant secret's *existence* by name if the policy allows tenant `Read`. That's by design — the policy is the authorization boundary, not the scope. If a tenant secret must be hidden from a unit even with cross-scope read grants, keep that secret out of `SecretScope.Tenant` entirely (or disable the fall-through for that deployment). This trade-off is explicit here and re-stated on `SecretsOptions.InheritTenantFromUnit`.

Change surface:

- No database migration. The registry schema is unchanged; inheritance is a pure resolver-layer concern.
- No HTTP endpoint change. List/create/delete responses still reflect only the owner-scope rows.
- No breaking change to the existing `ResolveAsync(SecretRef, ct)` signature.

## Revisit criteria

Reopen this decision when any of the following is true:

1. **A customer requires the "include" shape.** Concretely: a compliance audit flags the fall-through as an implicit grant that can't be expressed in their access-review tooling. At that point, add an explicit `InheritsFromTenant: string[]` on unit metadata as a parallel mechanism and let both coexist — the opt-out flag stays the sledgehammer.
2. **More than two scopes need to chain.** Today it's Unit → Tenant. If we add per-agent (#209) or per-region scopes that need to inherit further down the chain, redesign to a configurable scope chain rather than hard-coded Unit → Tenant.
3. **Policy evaluation cost dominates.** Today the resolver calls the policy twice per fall-through. If a real RBAC implementation makes that expensive, batch the two checks into a single `IsAuthorizedForScopesAsync` call and cache per-request.

## Priority

Shipped in wave 2. The inheritance path is the default; the opt-out flag is for day-zero operators who want strict isolation without waiting for a future setting.
