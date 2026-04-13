# 0004 — Per-agent secrets: keep "unit is the trust boundary" (do nothing for now)

- **Status:** Deferred — unit remains the trust boundary; no per-agent storage scope or agent-level ACL added in wave 2. Revisit criteria recorded below.
- **Date:** 2026-04-13
- **Closes:** [#209](https://github.com/savasp/spring-voyage/issues/209)
- **Related code:** `src/Cvoya.Spring.Core/Secrets/SecretScope.cs`, `src/Cvoya.Spring.Core/Secrets/ISecretResolver.cs`, `src/Cvoya.Spring.Dapr/Secrets/ComposedSecretResolver.cs`, [`0003-secret-inheritance-unit-to-tenant.md`](0003-secret-inheritance-unit-to-tenant.md)

## Context

`SecretScope` today covers `Unit`, `Tenant`, and `Platform`. Agents within a unit share access to the unit's secrets — `ISecretResolver` takes a `SecretRef` whose `OwnerId` is the unit name. #209 asked whether agents need finer-grained secret isolation inside a unit and enumerated three shapes:

1. **Add `SecretScope.Agent`.** New storage partition. Clean separation; registry rows keyed by agent id. Doubles resolve-path cost when an agent also wants unit-scoped secrets (two registry lookups per resolve) and creates a lifecycle question: agents are more ephemeral than units (clones, restarts, initiative re-spawn) — what happens to agent-owned secrets when an agent is recreated?
2. **Agent-level ACL over Unit scope.** Keep all rows under Unit scope but add a `VisibleToAgents: string[]?` column (null = all agents in the unit, populated = only listed). Single source of truth for per-unit secrets; per-agent isolation is a filter, not a separate partition. Access-control logic moves into the resolver.
3. **Do nothing.** Assert that the unit is the trust boundary. Users who need per-agent isolation spin up per-agent units.

Two pieces of context constrain the decision:

- **#204 / ADR 0003 (Unit → Tenant inheritance)** just landed and made the resolver the home of "which rows are visible to which caller." Adding a per-agent chain on top of the resolver is compositionally simple but materially widens the unit-test surface and the audit trail (every resolve now needs to record "this agent, passing this ACL, inheriting from tenant via policy X"). 
- **No concrete customer use case has been filed.** The issue enumerates speculative cases ("an agent-specific signing key") but the MVP multi-agent unit flows in the roadmap still use unit-level shared credentials.

## Decision

**Option 3 for now. The unit is the trust boundary.** No `SecretScope.Agent` value, no `VisibleToAgents` column, no agent-aware resolver logic. Every agent inside a unit sees the unit's full secret set and (via ADR 0003) any same-name tenant secrets the unit inherits.

Operators who need per-agent isolation today have a composable workaround already: spin up a sibling unit with one agent, and use tenant-scoped secrets only where sharing across those single-agent units is intentional. This reuses the unit boundary — which already has CRUD endpoints, audit logs, and RBAC hooks — instead of duplicating those for a second partition.

### Why option 3 over 1 and 2

#### Option 1 (new `SecretScope.Agent`)

Cons outweigh pros in the current shape of the system:

- **Lifecycle mismatch.** Agents are more ephemeral than units — they can be cloned, re-spawned by initiative, or re-hydrated after crashes. Every ephemeral lifecycle transition becomes a "does the agent's secret set survive?" question. The registry has no current notion of agent identity stability, and plumbing one through is a cross-cutting concern that #209 would inherit accidentally.
- **Read-path doubling.** Agents that need both their own and their unit's secrets would pay for two registry lookups per resolve. Combined with the Unit → Tenant fall-through from ADR 0003, a single "agent needs secret X" call could touch three registry entries, three policy checks, and three store reads. That's a 3x cost regression on a hot path that already runs inside every delegated-execution launch.
- **Enum churn on a domain type.** `SecretScope` models ownership. Adding `Agent` embeds the claim "agents own secrets" at the domain-contract layer. If the real requirement turns out to be "certain unit secrets are hidden from certain agents" (which is closer to the use cases cited in #209), `SecretScope.Agent` is the wrong shape and we'd have to ship option 2 alongside it.
- **Migrations and inheritance.** ADR 0003 leaves open the question of whether/how a fourth scope inherits. Adding `Agent` now forces a decision on agent → unit fall-through semantics that does not have a customer driver.

#### Option 2 (agent-level ACL over Unit scope)

Closer to the stated use case — "an agent needs its own API key that other agents in the same unit should NOT see" — but:

- **Authorization-in-resolver.** The current design keeps authorization in `ISecretAccessPolicy` (a DI extension point the private cloud implements). `VisibleToAgents: string[]?` puts a second, schema-shaped authorization in the registry row itself. That duplicates the responsibility and creates a coordination problem: when the RBAC implementation and the ACL column disagree, which wins? Today the policy is the single source of truth.
- **Requires an agent identity in the resolve call.** Today `ISecretResolver.ResolveAsync` takes a `SecretRef`. Option 2 needs the caller to pass an agent id into every resolve. That's a signature change that ripples through every actor, connector, and tool launcher — and couples the secret surface to the agent model in a way no other layer is today.
- **Doesn't solve the "agent needs its OWN key" case cleanly.** If the key is genuinely agent-specific (e.g. a per-agent signed webhook), it should be stored by the agent's creator at the agent's lifecycle granularity — but option 2 still stores under the unit, just with a filter. Deleting the agent doesn't delete the key, the rotation story is murky, and the list surface exposes keys that "belong to" agents that may no longer exist.

#### Option 3 (status quo)

Pros relative to 1 and 2:

- **Zero new surface.** No scope enum value, no schema column, no signature change, no additional policy hook.
- **Composable via existing primitives.** Per-agent isolation = one agent per unit. This doubles down on the unit as the isolation primitive and avoids inventing a new one speculatively.
- **Preserves the resolver's invariants.** Policy remains the authorization layer; registry remains the ownership layer; resolver remains the resolution strategy. ADR 0003 established that split, and this decision reinforces it.

Cons:

- **Suboptimal ergonomics when shared and agent-specific keys coexist within the same real unit.** A user who wants "shared for 3 agents, private for 1" must pick: split into two units and accept a unit-boundary crossing, or put all keys under the unit and accept that agent 4 can see the other three's keys.

That ergonomic cost is acceptable for wave 2; it becomes a real cost only when a concrete multi-agent workload files a use case.

## Consequences

- The single extensibility knob shipping in wave 2 is the Unit → Tenant fall-through from ADR 0003. Agent-level authorization is NOT layered into `ISecretAccessPolicy` — the policy's `ownerId` parameter is still "the scope owner" (unit name for Unit, tenant id for Tenant). Private cloud RBAC implementations can embed agent context in their principal evaluation if they want, but the OSS contract does not expose it.
- `SecretScope` remains three values — `Unit = 0`, `Tenant = 1`, `Platform = 2` — and is explicitly safe to append later without breaking callers (the enum is append-only per the parallel-agent conventions).
- Rotation (#201), audit-log decoration (#202), and encryption (#205) do NOT have to account for per-agent semantics in their designs. When this record is revisited, those three features will need review simultaneously — see revisit criteria below.
- Consumers of `ISecretResolver` today pass only a `SecretRef`. That signature is preserved; any future per-agent work will introduce a new method rather than mutate the existing one, to keep decorator stacks composable.

## Revisit criteria

Reopen this decision when **any** of the following is true:

1. **A concrete multi-agent unit use case files a need for per-agent secret hiding.** Not a hypothetical — an actual feature ticket ("agent X must run with credential A that agent Y, in the same unit, must not observe"). At that point, prefer option 2 (ACL over Unit scope) unless lifecycle analysis for the specific case points at option 1.
2. **Audit / compliance requires per-agent attribution.** If the audit-log decorator (#202) records agent-id on reads, and a customer audit requires that "agent X has NEVER read secret Y", the current model doesn't prevent the read — it just records who read it. If prevention is required, revisit.
3. **The private cloud RBAC implementation needs agent-id in `ISecretAccessPolicy.IsAuthorizedAsync`.** That would be an explicit signal that the authorization layer needs agent context; if so, add an overload with agent context rather than a new storage scope.

## Priority

Low. No code change in this PR. The revisit triggers above are concrete enough that future contributors can recognize them without re-running this analysis.
