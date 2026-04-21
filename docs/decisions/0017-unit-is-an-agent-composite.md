# 0017 — A Unit IS an Agent (composite pattern)

- **Status:** Accepted — `UnitActor` and `AgentActor` both implement the same addressable / message-receiving contract; a unit is indistinguishable from an agent at the messaging boundary.
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Core/IAddressable.cs`, `src/Cvoya.Spring.Core/Messaging/IMessageReceiver.cs`, `src/Cvoya.Spring.Dapr/Actors/AgentActor.cs`, `src/Cvoya.Spring.Dapr/Actors/UnitActor.cs`.
- **Related docs:** [`docs/architecture/units.md`](../architecture/units.md), [`docs/concepts/units.md`](../concepts/units.md).

## Context

V1 had a flat team structure: one team of expert agents and a leader, with bespoke routing through the leader. Hierarchical organisations (engineering → backend team → individual engineer; communities of practice; ad-hoc gatherings) were not expressible. v2 needed nesting from day one, but two shapes were on the table:

1. **Distinct `Agent` and `Unit` types.** Each has its own interfaces; routing knows which it's talking to and applies different code paths. Senders that want to send to "the unit" call into `UnitFacade.SendToTeam(...)`; senders that want to send to "an agent" go through `AgentDispatcher`.
2. **Composite pattern.** A unit IS an agent. Both types implement the same interfaces (`IAddressable`, `IMessageReceiver`); an address transparently resolves to either an `AgentActor` or a `UnitActor`; senders never know which they're talking to.

## Decision

**Adopt the composite pattern. `UnitActor` and `AgentActor` implement the same messaging interfaces and live behind the same address space. Routing, boundary checks, permissions, activity emission, and the orchestration spectrum all run uniformly regardless of whether the target is a leaf agent or a nested unit.**

- **Recursive composition is free.** A unit containing a unit containing a unit needs no special handling: the outer unit's `OrchestrationStrategy` picks one of its members; that member, if it's a unit, runs its own strategy; and so on. There is no "depth N" code path anywhere in the dispatcher.
- **Boundary opacity becomes a property of the composite.** A unit chooses how much of its internal structure to project (see [ADR 0008](0008-unit-boundary-decorator.md)). To external senders the unit looks exactly like an agent — same address shape, same message verbs.
- **Routing is single-hop.** A path address resolves to a flat actor id ([ADR 0023](0023-flat-actor-ids.md)); there is no multi-hop forwarding through each level of the hierarchy.
- **Skill projection follows the same rule.** Capabilities are enumerated through the expertise directory, not the agent roster ([ADR 0014](0014-skill-invoker-seam.md)); a unit's projected capabilities are first-class skills indistinguishable from a leaf agent's.

## Alternatives considered

- **Distinct `Agent` and `Unit` types with explicit delegation.** Every routing call site, every boundary enforcer, every activity emitter would have to branch on type. New consumer code (the MCP skill surface, the A2A gateway, the directory) would have to rediscover the same branch each time.
- **Unit as a coordinator service over a fixed roster.** Loses recursion and forces the routing layer to special-case "unit-of-units". Also defeats the boundary-as-decorator design: with a separate `UnitService`, every external read would need a parallel "is the caller inside?" check at each layer.

## Consequences

- **One mental model for senders.** "I send a message to an address" — whether the address resolves to a clone, a leaf agent, a unit, or a unit-of-units, the surface is identical.
- **The dispatcher is small.** No type-branching on `target.IsUnit`. The differentiation lives in the actor's `OnMessageAsync` implementation.
- **Boundary projection composes.** The boundary decorator ([ADR 0008](0008-unit-boundary-decorator.md)) wraps an aggregator whose recursion is itself uniform across leaf agents and nested units.
- **Skill catalog projects uniformly.** `expertise/{slug}` ([ADR 0014](0014-skill-invoker-seam.md)) names point at unit-projected capabilities and leaf-agent capabilities through the same naming scheme; the caller never has to know which.
