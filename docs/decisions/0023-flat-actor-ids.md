# 0023 — Flat actor ids; single-hop routing with directory resolution

- **Status:** Accepted — every actor has a flat globally-unique Dapr actor id; path addresses (`agent://team/sub/agent`) resolve to that id in one directory lookup; messages do not forward hop-by-hop through each unit in the path.
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Core/IAddressable.cs`, `src/Cvoya.Spring.Dapr/Routing/`, `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs` (member resolution).
- **Related docs:** [`docs/architecture/messaging.md`](../architecture/messaging.md), [`docs/architecture/units.md`](../architecture/units.md), [ADR 0017](0017-unit-is-an-agent-composite.md), [ADR 0008](0008-unit-boundary-decorator.md), [ADR 0013](0013-hierarchy-aware-permission-resolution.md).

## Context

Unit nesting can be deep — engineering → backend → payments → individual engineer is plausible. Two routing shapes were on the table:

1. **Multi-hop forwarding.** A message to `agent://eng/backend/payments/alice` is delivered to the `eng` unit, which forwards to `backend`, which forwards to `payments`, which finally hands it to `alice`. Each hop runs that unit's `OrchestrationStrategy`.
2. **Flat resolution + single-hop dispatch.** Path is resolved to a flat actor id in one directory lookup; the message goes directly to that actor; permission and boundary checks run once at resolution time.

The composite-pattern decision ([ADR 0017](0017-unit-is-an-agent-composite.md)) made flat resolution viable — every actor has the same shape, so a single id is enough. The remaining question was governance: where do boundary opacity, permission walks, and directory consistency hook in?

## Decision

**Path addresses resolve to a flat Dapr actor id in a single directory lookup. Messages dispatch directly to that actor; the path is never traversed hop-by-hop. Boundary opacity ([ADR 0008](0008-unit-boundary-decorator.md)) and hierarchy-aware permissions ([ADR 0013](0013-hierarchy-aware-permission-resolution.md)) walk the path at resolution time, then the dispatch is single-hop.**

- **Performance.** O(path depth) for the permission walk vs. O(N hops) for forwarding. The permission walk is in-memory once the directory cache is warm; forwarding would be N round-trips through N actors.
- **Simplicity.** Forwarding has compound failure modes: what happens if an intermediate unit is unhealthy? What if it has a long mailbox queue? Direct dispatch has none of those modes.
- **Permission enforcement is one place.** The boundary + permission walk happens at resolution time, returning either an actor id or a structured deny. The dispatch path itself does not re-check.
- **Cache-friendly.** Each unit caches member-name → actor-id mappings; directory mutation events ([`docs/architecture/messaging.md`](../architecture/messaging.md)) keep caches fresh with millisecond-grade consistency.

## Alternatives considered

- **Multi-hop forwarding with per-hop policy.** Distributes governance across N actors; every change to permission semantics has to be applied N times. Compound latency. Rejected.
- **Path-shaped Dapr actor ids (`unit:eng/unit:backend/agent:alice`).** Tempting because a path is "naturally" hierarchical, but Dapr placement is per-id, not per-prefix; you do not gain locality by sharing a prefix, and resolution still needs the directory anyway.

## Consequences

- **Directory is on the critical path.** A message cannot route without the resolution lookup. The directory's cache + invalidation contract is load-bearing for routing latency.
- **Permission walks see the full path.** Hierarchy-aware permission resolution ([ADR 0013](0013-hierarchy-aware-permission-resolution.md)) walks from root to leaf at resolution time; the inherit-by-default / nearest-grant-wins rules apply uniformly across nesting depths.
- **Boundary projection runs once per resolution.** The boundary decorator ([ADR 0008](0008-unit-boundary-decorator.md)) wraps the directory at the resolution layer; the dispatch path never sees opacity questions because they are already baked into "does this address resolve at all?"
- **Failure modes are simple.** A delivery failure is "the target actor failed" or "the target actor is unreachable" — never "the second hop in a five-hop forwarding chain timed out."
