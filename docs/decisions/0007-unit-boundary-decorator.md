# 0007 — Unit boundary as decorator over the expertise aggregator

- **Status:** Accepted — a `BoundaryFilteringExpertiseAggregator` decorator wraps the base `ExpertiseAggregator`, reading per-unit `UnitBoundary` rules on every read and applying opacity / projection / synthesis to the outside-the-unit view only.
- **Date:** 2026-04-16
- **Closes:** [#413](https://github.com/cvoya-com/spring-voyage/issues/413)
- **Builds on:** [ADR 0006](0006-expertise-directory-aggregation.md) (recursive aggregation).
- **Related code:** `src/Cvoya.Spring.Core/Capabilities/UnitBoundary.cs`, `src/Cvoya.Spring.Core/Capabilities/BoundaryViewContext.cs`, `src/Cvoya.Spring.Core/Capabilities/IUnitBoundaryStore.cs`, `src/Cvoya.Spring.Core/Capabilities/IExpertiseAggregator.cs` (caller-aware overload), `src/Cvoya.Spring.Dapr/Capabilities/BoundaryFilteringExpertiseAggregator.cs`, `src/Cvoya.Spring.Dapr/Capabilities/ActorBackedUnitBoundaryStore.cs`, `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs` (GetBoundary / SetBoundary), `src/Cvoya.Spring.Host.Api/Endpoints/BoundaryEndpoints.cs`, `src/Cvoya.Spring.Cli/Commands/UnitBoundaryCommand.cs`.

## Context

PR-PLAT-BOUND-1 (#412, ADR 0006) shipped the recursive expertise aggregator. Its implementer's note explicitly called out that a boundary layer should slot in as a decorator over `IExpertiseAggregator` — without changing the interface or the walk — because every `ExpertiseEntry` already carries `Origin` and `Path`, which is exactly what a boundary rule needs to match against.

Phase 5 semantics (docs/architecture/units.md § Unit Boundary) describe four shape requirements:

1. **Opacity** — a unit can mark members or capabilities as hidden to outside callers.
2. **Projection** — a unit can expose capabilities under different names / levels / descriptions.
3. **Filtering** — whether a caller is inside or outside the unit decides which view they see.
4. **Synthesis** — a unit can emit a derived aggregate (e.g. "team-level React expertise") instead of raw per-member entries.

Four design questions needed answers:

1. **Where do the rules live?** On the unit actor (local state), in a separate repository table, or on the directory entity?
2. **How do boundary rules reach the aggregator?** Re-read on every call, cache alongside aggregates, or event-driven?
3. **How does the decorator distinguish inside vs outside callers?** New parameter on the existing method, new overload, or a new interface?
4. **How does the shape keep #414 (hierarchy-aware permission checks) unblocked?** The filtering needs a caller identity that's richer than a bool.

## Decision

**Introduce `UnitBoundary` (core) + `IUnitBoundaryStore` (store seam) + `BoundaryFilteringExpertiseAggregator` (decorator over the base aggregator) + `BoundaryViewContext` (caller identity).** The decorator registers as the default `IExpertiseAggregator` via DI; call sites that resolve the interface get boundary-aware behaviour for free, and the raw aggregator stays available as a concrete singleton for tests that want the pre-decorator view.

### Rule shape

`UnitBoundary` is a record with three nullable collections: `Opacities`, `Projections`, `Syntheses`. Every rule uses the same small matching vocabulary — a `DomainPattern` and an `OriginPattern`, each supporting a trailing `*` for prefix match and case-insensitive otherwise. Keeping the matchers uniform means one helper in the decorator and one YAML shape in the operator surface.

- **`BoundaryOpacityRule(DomainPattern?, OriginPattern?)`** — strips matching entries from the outside view.
- **`BoundaryProjectionRule(DomainPattern?, OriginPattern?, RenameTo?, Retag?, OverrideLevel?)`** — rewrites matching entries. Origin and path are preserved so downstream permission checks (#414) still see the true contributor.
- **`BoundarySynthesisRule(Name, DomainPattern?, OriginPattern?, Description?, Level?)`** — collapses matches into a single unit-attributed entry. When no member matches the rule, the capability is **not** fabricated.

An empty boundary (every slot null / empty) is the "transparent" default and the decorator is a straight pass-through. Repositories treat an empty boundary as a row deletion.

### Storage

`IUnitBoundaryStore` abstracts persistence so the private cloud repo can layer a tenant-scoped or cached store without touching the decorator. The default implementation reads and writes via the unit actor (`UnitActor.GetBoundaryAsync` / `SetBoundaryAsync`, backed by `StateKeys.UnitBoundary`). Actor state is already the source of truth for unit own-expertise, connector binding, and metadata — boundary is the fourth member of that family.

### Decorator pattern

`BoundaryFilteringExpertiseAggregator` wraps the base `ExpertiseAggregator` and implements the new caller-aware overload on `IExpertiseAggregator`:

```csharp
Task<AggregatedExpertise> GetAsync(Address unit, BoundaryViewContext context, CancellationToken ct = default);
```

The legacy `GetAsync(Address, CancellationToken)` remains — it delegates straight to the inner aggregator and never consults the boundary, preserving every existing call site and keeping NSubstitute mocks in tests compiling without modification.

**Rule order in the decorator:**

1. **Opacity first** — opaque entries are stripped. Opacity wins over projection and synthesis.
2. **Synthesis second** — matching (non-opaque) entries are consumed and replaced with a single synthesised entry.
3. **Projection third** — remaining (non-opaque, non-synthesised) entries are rewritten.

This ordering is deterministic and keeps the three features independent: adding a new projection rule never changes which entries are hidden or synthesised; adding a synthesis rule never re-opens an opaque entry.

### Caller identity

`BoundaryViewContext(Caller, Internal)` carries the minimum data the decorator needs today. A caller with `Internal = true` bypasses all boundary rules — the unit itself and its descendants see the raw aggregate. An outside caller (or an unauthenticated one — the safe default) sees the filtered view. The shape is intentionally small: #414 will grow it with permission-aware fields (deep-access rights, unit-scoped operator roles) without touching the aggregator interface again.

### Invalidation

Boundary writes call `IExpertiseAggregator.InvalidateAsync` to evict stale inner-aggregator snapshots — the decorator reads boundary rules on every call, but the inner aggregate is still cached per ADR 0006. A boundary write doesn't restructure the member graph; eviction keeps the caller's post-write read consistent.

## Alternatives considered

- **Add a `visibility` enum on each `ExpertiseEntry`.** Pros: a single field expresses opacity. Cons: projection and synthesis don't fit on a single entry — projection rewrites, synthesis replaces — so the shape would not generalise. Rejected.
- **Bake rule evaluation into the base aggregator walk.** Pros: one class, no decorator. Cons: the walk already handles the cycle guard, dedup, level-winner, cache; threading boundary logic through it would make it harder to reason about and harder for the private cloud repo to replace. Rejected — decoration preserves the layered design ADR 0006 explicitly left room for.
- **Boundary cache alongside the aggregate cache.** Pros: one fewer store read per decorated call. Cons: two caches now have to stay in sync and the boundary is small (a handful of rules per unit) so the benefit is marginal. Rejected for now — the interface accepts a cached store later.
- **Per-dimension endpoints (`/boundary/opacity`, `/boundary/projection`, `/boundary/synthesis`).** Pros: narrower write surface. Cons: the three dimensions are usually edited together and a unified endpoint matches the `/policy` precedent (see ADR 0006's discussion). The CLI already knows how to merge a single-slot edit into a full body; doubling the HTTP surface without unlocking anything is not worth it. Rejected.
- **Boundary evaluation on read by the aggregator consumer.** Pros: the aggregator returns the raw view and each consumer (HTTP endpoint, MCP, orchestration strategy) applies its own filtering. Cons: rules get duplicated across call sites; a new consumer that forgets the filter leaks opaque entries. Rejected — one decorator, one set of rules.

## Consequences

- **#414 (hierarchy-aware permission checks) is unblocked.** It plugs into `BoundaryViewContext` to derive `Internal` from the authenticated principal's relationship to the unit. The aggregator interface does not need to change.
- **The OSS CLI and HTTP API have feature parity out of the box.** `spring unit boundary get|set|clear` targets the same unified `/api/v1/units/{id}/boundary` endpoint; both surfaces accept the same YAML fragment shape.
- **The private cloud repo extends cleanly.** It can register its own `IUnitBoundaryStore` (e.g. tenant-scoped DB-backed) via `TryAdd`, or wrap the decorator with tenant-aware caller-context resolution. No call site changes required.
- **The portal dashboard needs a boundary card.** Deferred to a follow-up so this PR stays focused on the contract + CLI.
- **Existing integration tests continue to pass unchanged** because the caller-aware overload has a default implementation that delegates to the legacy `GetAsync(address)` method — every substitute mock and every call through `IExpertiseAggregator` works without touching them.
