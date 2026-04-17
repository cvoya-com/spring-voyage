# 0006 — Recursive expertise directory aggregation

- **Status:** Accepted — single `IExpertiseAggregator` walks the member graph with depth-capped cycle guard and cache-on-read semantics.
- **Date:** 2026-04-16
- **Closes:** [#412](https://github.com/cvoya-com/spring-voyage/issues/412)
- **Related code:** `src/Cvoya.Spring.Core/Capabilities/IExpertiseAggregator.cs`, `src/Cvoya.Spring.Core/Capabilities/ExpertiseEntry.cs`, `src/Cvoya.Spring.Core/Capabilities/AggregatedExpertise.cs`, `src/Cvoya.Spring.Dapr/Capabilities/ExpertiseAggregator.cs`, `src/Cvoya.Spring.Dapr/Capabilities/ActorBackedExpertiseStore.cs`, `src/Cvoya.Spring.Host.Api/Endpoints/ExpertiseEndpoints.cs`, `src/Cvoya.Spring.Cli/Commands/ExpertiseCommand.cs`

## Context

Phase 5 (#98, #160) shipped nested units and M:N agent membership. The directory service holds address → entry mappings, but has no notion of expertise composition: a parent unit cannot ask "what capabilities does this sub-tree cover?" and a root unit has no way to discover which leaf agent covers `python/fastapi`. Without that composition, peer lookup (`discoverPeers`) is limited to role-based multicast over the flat directory and cannot reflect the organisational structure the unit hierarchy encodes.

The shape of what was needed came out of issue #412 and the downstream work that blocks on it:

- **PR-PLAT-BOUND-3** (#414, hierarchy-aware permission checks) needs to tell, for each capability, which sub-unit an enclosing unit traversed through to reach the leaf — otherwise the permission model cannot distinguish "agent-scope read" from "unit-scope read".
- **PR-PLAT-BOUND-2** (#413, unit boundary) needs a composable input it can project, filter, or synthesize — it doesn't want to re-walk the graph itself.
- **Phase-6 PR-PLAT-DOMAIN-1** (#417, research domain package) needs to resolve capabilities across a multi-level research cell.

Four design questions needed answers:

1. **Where does the recursive walk live?** On the `UnitActor` (so every unit self-composes on read), as a separate service, or projected into the persistence layer?
2. **What does an aggregated entry look like?** Flat list of domains, or each entry pinned with its contributor and path?
3. **How do changes propagate?** Event fan-out from every actor edit, synchronous cache invalidation on write, or pull-with-TTL?
4. **How does this not preclude #413 (boundary projection/filtering)?** The aggregator should produce the transparent view; boundary rules layer on top.

## Decision

**Introduce a single `IExpertiseAggregator` DI service that walks the member graph at read time, caches per unit, and invalidates up the ancestor chain on write.** Each aggregated entry pins its contributing origin address and the full path from the aggregating unit down to the origin.

The aggregator is defined in `Cvoya.Spring.Core.Capabilities` (dependency-free) and implemented in `Cvoya.Spring.Dapr.Capabilities`. It composes three reads at each frame: the current entity's own domains (through `IExpertiseStore`), the unit's members (through `IUnitActor.GetMembersAsync` via the directory), and the frame's path-so-far (kept in the walk state).

### Walk contract

- **Breadth-first**, starting from the requested unit; each frame carries the ordered path of addresses walked from the root of the aggregation to the current node.
- **Depth-capped** at `UnitActor.MaxCycleDetectionDepth = 64`, matching the membership cycle-detection bound in `UnitActor.AddMemberAsync`. Exceeding the cap throws `ExpertiseAggregationException` carrying the path — the same failure shape the membership path uses.
- **Cycle guard** by actor id. A back-edge onto the aggregating unit throws; benign DAG convergence on an intermediate node is skipped via a visited set.
- **Store errors are non-fatal.** A transient actor/directory read failure logs a warning and treats the contributor as "no expertise," so one flaky leaf doesn't fail the whole aggregate.

### Entry shape

Each `ExpertiseEntry` is `(Domain, Origin, Path)`:

- `Origin` is preserved as-seen — not rewritten to point at the parent — so a consumer (e.g. `#414`) can follow the origin one hop at a time.
- `Path` is `[aggregating-unit, …, origin]`. Path length − 1 is the depth. The boundary layer (#413) will consume this to decide what to project vs. filter.
- Duplicate `(domain-name, origin)` pairs are collapsed. When levels disagree on the collapsed pair, **the stronger level wins** so "closest to the root" never silently downgrades an `expert` contribution.

### Cache + invalidation

- Aggregated snapshots are kept in a per-process `ConcurrentDictionary` keyed by unit address.
- Writes at any entity (agent expertise, unit own expertise, member add/remove, agent assign/unassign) call `IExpertiseAggregator.InvalidateAsync(origin)`.
- `InvalidateAsync` walks **up** — for agent origins through `IUnitMembershipRepository.ListByAgentAsync`, for unit origins by scanning the directory for units whose members list contains the child — and evicts each ancestor.
- No TTL. Aggregations are small, writes are rare, invalidation is precise.

### Store seam

`IExpertiseStore.GetDomainsAsync(Address)` reads through actor proxies (`IAgentActor.GetExpertiseAsync`, `IUnitActor.GetOwnExpertiseAsync`) resolved via the directory. Keeping the store abstract lets the private cloud repo layer a tenant-scoped or observed implementation without touching the aggregator.

### Boundary seam (deferred)

The aggregator produces the **transparent** view of the sub-tree. `#413` will add boundary rules as a filter/projection layer between the aggregator and its consumer; the aggregator interface itself doesn't need to change to accommodate opacity, because each `ExpertiseEntry` already carries the path the boundary rules will need to inspect.

## Alternatives considered

- **Walk on the `UnitActor` itself.** Pros: avoids a new service. Cons: every aggregating read becomes a cross-actor call per level with no shared cache; cycle guard + path bookkeeping has to live on each actor; the aggregator behaviour becomes hard to swap in the private cloud repo. Rejected.
- **Compile aggregation into a database view.** Pros: a single query per read. Cons: the authoritative source of expertise is actor state, not the SQL entities; the view would chase a rolling target. Rejected as premature.
- **Event-driven cache fan-out.** Pros: eventual consistency without invalidation calls. Cons: every actor edit now has to publish an "expertise-changed" event; the in-process cache has to subscribe; race windows where a read returns stale data just after a write. Rejected for now — the invalidation walk is cheap and deterministic. A Phase-6 event-sourced variant can replace it without touching the interface.
- **Flat entries without origin.** Pros: simpler wire shape. Cons: `#414` is blocked — you can't permission-check a capability you can't attribute. Rejected.
- **Unbounded recursion.** Pros: trivially simple. Cons: a misconfigured graph kills the host. Rejected — 64-level cap matches the membership bound.

## Consequences

- **`#413` boundary layer** plugs in as a filter over `AggregatedExpertise` without changing the aggregator.
- **`#414` hierarchy-aware permission checks** has the information it needs: `ExpertiseEntry.Origin` and `ExpertiseEntry.Path` are both on the wire, both in the typed result.
- **Write paths** (agent assign / unassign, unit add / remove member, expertise set) now call `InvalidateAsync`. Omitting the call means peer-lookup reads stale aggregates until the process recycles — caught by integration tests that invalidate explicitly.
- **Depth cap** must stay aligned with `UnitActor.MaxCycleDetectionDepth`. The implementation references that constant directly so a future change there propagates automatically.
- **CLI / UI parity.** The `spring agent expertise` / `spring unit expertise` subcommands cover the same surface as the HTTP endpoints. Portal UI parity is tracked separately in [#486](https://github.com/cvoya-com/spring-voyage/issues/486).
