# 0010 — Manifest-driven orchestration-strategy selection resolves per message

- **Status:** Accepted — `orchestration.strategy` on the manifest persists to `UnitDefinitions.Definition`; `UnitActor` consults `IOrchestrationStrategyResolver` per domain message; `UnitPolicy.LabelRouting` infers `label-routed` when no manifest key is declared.
- **Date:** 2026-04-17
- **Closes:** [#491](https://github.com/cvoya-com/spring-voyage/issues/491)
- **Related code:** `src/Cvoya.Spring.Manifest/UnitManifest.cs`, `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyProvider.cs`, `src/Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyResolver.cs`, `src/Cvoya.Spring.Dapr/Orchestration/DbOrchestrationStrategyProvider.cs`, `src/Cvoya.Spring.Dapr/Orchestration/DefaultOrchestrationStrategyResolver.cs`, `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs`, `src/Cvoya.Spring.Host.Api/Services/UnitCreationService.cs`

## Context

PR-PLAT-ORCH-1 (#493) shipped the `label-routed` strategy as a keyed scoped `IOrchestrationStrategy` but left the selection wiring in the host — every `UnitActor` still activated with the unkeyed default strategy cached on the actor instance. #491 closes the gap by giving operators a manifest-driven way to pick a strategy per unit, without touching the host or writing code.

Three design questions needed answers before the selector could ship:

1. **Where does the selection happen — at activation, or per message?** Resolving once at activation is fastest but caches the decision for the actor's lifetime; resolving per message costs a DI scope build but lets scoped strategies see hot policy edits.
2. **How does the manifest slot relate to `UnitPolicy.LabelRouting`?** A unit with a `LabelRouting` policy but no manifest `orchestration.strategy` is obviously asking for label-routing — forcing operators to set both would be bad ergonomics.
3. **What happens when the manifest declares a key that isn't registered?** Hard-fail the message, log-and-fall-through, or pretend the slot is absent?

## Decision

**Per-message resolution through an injected `IOrchestrationStrategyResolver`, with a three-step precedence ladder (manifest key → `LabelRouting` inference → unkeyed default) and degraded-but-alive behaviour on misconfiguration.**

### Per-message resolution

The resolver builds a new `IServiceScope` for every domain message `UnitActor.HandleDomainMessageAsync` dispatches, resolves the right keyed `IOrchestrationStrategy` out of that scope, and disposes the scope when the orchestration call returns. Cost: one DI scope per unit turn. Benefit:

- **Hot policy edits show up immediately.** `LabelRoutedOrchestrationStrategy` is scoped because it depends on the scoped `IUnitPolicyRepository`. An operator who updates the trigger map through `spring unit policy label-routing set` sees the change on the next message without recycling the actor.
- **No manifest-reload handshake on re-apply.** A re-apply of the same manifest that flips `orchestration.strategy` from `ai` to `label-routed` takes effect on the next message instead of requiring actor deactivation.
- **The Dapr actor lifetime is untouched.** We never had to teach `UnitActor` how to recycle itself when its declarative inputs change — the resolver re-reads on every turn.

Alternatives considered and rejected:

- **Cache the resolved strategy on the actor.** Fastest dispatch path but every "did the manifest change?" signal becomes a new cache-invalidation vector. The extra scope build is measured in microseconds; the hot-edit benefit is worth it.
- **Resolve at activation.** Same problem as the cache, plus it binds the decision to actor lifetime which the platform does not otherwise control.

### Precedence ladder

1. **Manifest `orchestration.strategy` wins** when a DI registration under that key exists. This is the explicit operator intent.
2. **`UnitPolicy.LabelRouting` non-null → `label-routed`** when no manifest key was declared. ADR-0007 wrote the revisit criterion: "when the manifest-driven strategy selector lands, `LabelRouting` should imply `strategy: label-routed` by default so operators don't have to set both." This keeps the `spring unit policy` surface (#453) coherent with the manifest.
3. **Unkeyed default** (the platform's `ai` strategy) when neither (1) nor (2) apply. Matches pre-#491 behaviour so every existing unit keeps dispatching the same way.

Alternatives considered and rejected:

- **Policy beats manifest.** Inverted precedence would let a `spring unit policy` edit silently override a manifest declaration — a quiet way to drift the live unit away from the committed YAML. Manifests are the versioned artefact; they should win on conflict.
- **No inference.** Forcing operators who already configured `LabelRouting` to add `orchestration.strategy: label-routed` to the manifest too was the subject of the ADR-0007 revisit criterion. Matching-intent inference is the friendlier default.

### Degraded-but-alive on misconfiguration

A manifest key declared but not registered in DI is treated as "no manifest directive", not an error: the resolver logs a warning, continues to step 2, and finally to step 3. Rationale:

- **Operator renames.** If a host retires a custom strategy key it registered itself, every unit that once declared that key should keep dispatching through the default rather than failing every message.
- **Private-cloud overlays.** The cloud host may register tenant-specific keys a given tenant does not have. The dev-time manifest should still deploy rather than 500.
- **Observability.** The warning log is the discovery path — the operator fixes the manifest without downtime.

Hard-fail was the alternative. Rejected: orchestration is the hot path; a misconfigured key should not take the whole unit offline when the default would have worked.

## Consequences

- **New extension points.** `IOrchestrationStrategyProvider` and `IOrchestrationStrategyResolver` are both `TryAdd`'d so the private cloud repo can swap in a tenant-scoped reader (e.g. a provider that consults a per-tenant strategy registry) without forking the OSS default.
- **In-process cache in front of the DB provider (#518).** The default `IOrchestrationStrategyProvider` is a `CachingOrchestrationStrategyProvider` decorator wrapping `DbOrchestrationStrategyProvider`. The decorator uses a hybrid shape — a short TTL (30s) plus an explicit invalidation hook (`IOrchestrationStrategyCacheInvalidator`) — so in-process writes (`UnitCreationService.PersistUnitDefinitionOrchestrationAsync`) see immediate consistency and cross-process writes from a sibling replica heal within the TTL without cross-host coordination. Stampede protection coalesces concurrent misses through a per-unit semaphore. The concrete `DbOrchestrationStrategyProvider` remains resolvable by its concrete type for cache-off testing. Cross-host invalidation (e.g. broadcasting a manifest write from the API host to the Worker host) is deferred — the TTL covers it today.
- **Actor-constructor additive change.** `UnitActor` takes the resolver as an optional `IOrchestrationStrategyResolver?` constructor argument so every existing test harness that constructs the actor directly keeps compiling. The unkeyed `IOrchestrationStrategy` parameter remains the final fallback both for tests and for units whose resolver produced nothing (e.g. when the DB provider fails transiently).
- **Manifest grammar is additive.** `orchestration:` is a new nullable section on `UnitManifest`. Existing manifests without the block keep the historical behaviour.
- **No new wire surface.** The selector is read-only from the unit actor's perspective; operators configure it through the existing manifest-apply path (`spring apply` / `/api/v1/units/from-yaml` / `/api/v1/units/from-template`). A dedicated `GET/PUT /api/v1/units/{id}/orchestration` endpoint and CLI command are deliberately not introduced here — if inspection proves necessary, it can be added in a later PR without reshaping the persisted slot.

## Revisit criteria

- **Per-strategy options on the manifest.** If operators need to pass per-strategy knobs (workflow image digest, label-routed default timeout) we grow `OrchestrationManifest` with additional optional fields — the shape was chosen as a class, not a bare string, exactly so this is additive.
- **Third connector payload shape.** When a strategy other than `label-routed` needs to read a payload-extraction plug-in, we hoist extraction into an injected service the resolver can pass to the strategy instead of baking it into the strategy class.
- **Per-message strategy overrides.** If a caller needs to force a strategy override for a specific message (e.g. a "run with AI this one time" debug lever), add an optional `X-Spring-Strategy-Override` header / wire field that bypasses the resolver's precedence ladder. Out of scope for #491.
- **Cross-host cache invalidation.** The caching decorator (#518) heals cross-process writes within its TTL window. If operators start reporting that sibling replicas take "too long" to pick up a manifest edit, add a Dapr pub/sub broadcast on the write path (API host publishes → every worker's invalidator subscribes) and shrink or drop the TTL.
