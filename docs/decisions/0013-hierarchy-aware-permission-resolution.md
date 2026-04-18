# 0013 — Hierarchy-aware permission resolution (inheritance-by-default, nearest-grant-wins, fail-closed)

- **Status:** Accepted — `IPermissionService.ResolveEffectivePermissionAsync` walks parent units so ancestor grants cascade down to descendants by default. A per-unit `UnitPermissionInheritance` flag (`Inherit` / `Isolated`) acts as the permission-layer analogue of an opaque boundary. The resolver fails closed when the inheritance flag is unreadable. `PermissionHandler`, `MessageRouter`, and the activity-stream SSE endpoint all use the hierarchy-aware resolver.
- **Date:** 2026-04-17
- **Closes:** [#531](https://github.com/cvoya-com/spring-voyage/issues/531)
- **Implemented by:** [PR #533](https://github.com/cvoya-com/spring-voyage/pull/533) (closes [#414](https://github.com/cvoya-com/spring-voyage/issues/414))
- **Related:** [ADR 0008](0008-unit-boundary-decorator.md) (unit boundary opacity / projection / synthesis — the *data* analogue of this *permission* inheritance), [#413](https://github.com/cvoya-com/spring-voyage/issues/413) and [#497](https://github.com/cvoya-com/spring-voyage/issues/497) (boundary opacity semantics), [ADR 0003](0003-secret-inheritance-unit-to-tenant.md) (secret fall-through precedent).
- **Related code:** `src/Cvoya.Spring.Core/Authorization/IPermissionService.cs`, `src/Cvoya.Spring.Core/Units/IUnitHierarchyResolver.cs`, `src/Cvoya.Spring.Dapr/Auth/UnitPermissionInheritance.cs`, `src/Cvoya.Spring.Dapr/Auth/UnitPermissionEntry.cs`, `src/Cvoya.Spring.Dapr/Auth/PermissionHandler.cs`, `src/Cvoya.Spring.Dapr/Routing/MessageRouter.cs`, `src/Cvoya.Spring.Host.Api/Endpoints/ActivityStreamEndpoints.cs`, `docs/architecture/security.md` § *Hierarchy-aware permission resolution*.

## Context

Before PR #533, `IPermissionService.ResolvePermissionAsync` was **direct-only**: a `(humanId, unitId)` lookup returned the grant on that unit or nothing. Nested organisations suffered for it — a tenant owner who owned the root unit had no grants on any child unit they had not explicitly enumerated. Operators worked around this by copying grants down the tree, which drifted immediately.

The pre-#414 security document sketched an **opt-in** inheritance model (`permissions.inherit: parent` on each child), echoing how Java servlet containers or traditional ACL systems have historically modelled cascading authority. The real-world organisations we had onboarded did the opposite: they *assumed* ancestor grants flowed, and had to be actively reminded they did not. Making inheritance opt-in reversed the ergonomic default.

Four design questions had to be resolved before landing the hierarchy walk:

1. **Inheritance default.** Opt-in (explicit `Inherit`) or opt-out (explicit `Isolated`)?
2. **Walk semantics.** If multiple ancestors grant, does the walk take the **nearest** grant, the **strongest** grant, or some merge?
3. **Failure mode.** What happens if the inheritance flag cannot be read on a hop (state-store outage, concurrent actor tear-down)?
4. **Relationship to ADR 0008's boundary.** The boundary decorator already hides *data* from outside callers. Do we reuse the boundary record for permission isolation, or add a dedicated flag?

## Decision

**Ancestor grants cascade to descendants by default. Inheritance can be turned off per unit via a dedicated `UnitPermissionInheritance` flag. The walk returns the *nearest* ancestor grant. The resolver fails closed on unreadable inheritance state.**

### Resolution rules

The walk from a target unit to the root applies these rules in order (also enumerated in `docs/architecture/security.md` § *Hierarchy-aware permission resolution*):

1. **Direct grants on the target unit always win** — including deliberate downgrades. A child that grants a human `Viewer` is never silently promoted to `Owner` because an ancestor grants `Owner`. Operators need a way to *reduce* authority on a specific subtree, and only a direct-grant override can express that.
2. **Nearest ancestor grant wins.** If the target has no direct grant, the resolver walks nearest ancestor first and returns the first non-null grant it finds. Depth does not amplify permissions: a parent granting `Operator` cascades as `Operator`, not as `Owner`. This keeps the composition predictable — grants accumulate by *proximity*, not by union.
3. **`Isolated` stops the walk.** A unit marked `Isolated` blocks ancestor authority from flowing through it. Direct grants on the isolated unit still apply. Direct grants on its *own* descendants still cascade through it normally (isolation is a one-hop gate, not a whole-subtree seal).
4. **Fail closed.** If the platform cannot read the inheritance flag on a hop (state-store outage, mid-activation actor), the walk treats that hop as `Isolated` and blocks the ancestor grant. A 403 is cheaper than a confused deputy.
5. **Depth bounded.** The walk is capped at `UnitActor.MaxCycleDetectionDepth` (64) so a pathological graph cannot loop or silently promote a caller.

### Relationship to boundary opacity (ADR 0008 / #413 / #497)

Boundary opacity (ADR 0008) answers "what data does an outside caller see of this unit?". Permission inheritance answers "whose authority flows through this unit?". They are orthogonal:

- A unit can be **data-opaque but permission-inherited** (an R&D squad that hides in-flight projects from the rest of the org but trusts the org owner as its owner).
- A unit can be **data-transparent but permission-isolated** (a compliance team whose roster is public but whose audit grants cannot be acquired by accident via ancestor cascade).

We considered reusing the `UnitBoundary` record with a new `BoundaryPermissionRule` slot so operators would have "one place to configure opacity". Rejected: the two axes get toggled on completely different cadences and by different operator personas (security / governance vs. information architecture). Collapsing them would force every boundary edit to think about permissions and vice versa. A dedicated `UnitPermissionInheritance` flag on the unit actor state keeps the security-sensitive slot small and audit-friendly.

### Why not extend `UnitPolicy` / `IUnitPolicyEnforcer`

`UnitPolicy` is the governance record (label routing, cloning policy, initiative levels). The permission resolver runs *before* any policy check — it answers "is this caller authorised to even have a policy evaluated?". Overlaying permission inheritance onto `UnitPolicy` would invert that layering and entangle authorisation with governance. The permission layer stays separate from `IUnitPolicyEnforcer`.

### `IUnitHierarchyResolver` DI seam

A new `IUnitHierarchyResolver` in `Cvoya.Spring.Core/Units` abstracts the parent walk. The default implementation scans the directory (matching the pattern ADR 0006 / PR #487 set for the expertise aggregator). Downstream deployment repositories can register a materialised parent index (e.g. Cosmos DB–backed) via `TryAdd` without touching `PermissionHandler`, `MessageRouter`, or the activity-stream endpoint.

## Alternatives considered

- **Opt-in inheritance (original pre-#414 design).** Operators must set `permissions.inherit: parent` on every child. Matches traditional ACL defaults. Rejected: empirically operators *expect* cascade and discover the wrong default only after a failed escalation. Inheritance-by-default + explicit `Isolated` opt-out is the friendlier shape and also matches how ADR 0003 handled secret fall-through.
- **Strongest-grant-wins across the full chain.** Walk every ancestor, keep the highest permission seen. Simple, predictable, and wrong: it makes it impossible to downgrade a subtree — a grandparent `Owner` grant would always beat an intervening `Viewer`. Rejected.
- **Fail open when inheritance state is unreadable.** Assume `Inherit` on a read fault so permissions don't drop silently. Rejected: the platform already tolerates read retries and a confused-deputy escalation is strictly more expensive than an extra 403.
- **Reuse `UnitBoundary` with a permission slot.** Rejected per the orthogonality argument above.
- **Layer it on top of `UnitPolicy`.** Rejected because `UnitPolicy` runs inside the authorised call, not before it.

## Consequences

- **Operators get the cascade they expected.** An owner grant on a tenant root flows to every descendant that has not explicitly isolated itself.
- **Subtree downgrades are expressible.** A child unit can still bind a human to `Viewer` even under an ancestor `Owner`, because direct grants win.
- **The isolation flag is audit-grade.** A compliance subtree marks itself `Isolated` once, in a dedicated slot on the unit actor state, with a dedicated state-key round-trip — it does not hide inside a boundary YAML fragment.
- **Fail-closed posture holds across the three call sites.** HTTP authorisation, cross-unit messaging, and activity-stream subscriptions all call the same resolver; a read failure denies in all three consistently.
- **Forward compatibility with per-action inheritance.** The flag is a three-valued shape today (`Inherit` / `Isolated`; null is treated as `Inherit` for pre-existing units). If we later need per-action granularity (e.g. "inherit viewer, not operator"), we extend the flag to a record; call sites that consume `UnitPermissionInheritance` stay source-compatible.
- **Matches ADR 0008's decorator split.** ADR 0008 filters *data* at read time through `BoundaryFilteringExpertiseAggregator`; this ADR filters *authority* at resolve time through the hierarchy resolver. Neither leaks into the other, and both can be swapped independently by the downstream deployment repo.
