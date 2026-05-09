# 0026 — Per-agent container scope (one container per agent, not per unit)

- **Status:** Accepted — 2026-04-22 — `A2AExecutionDispatcher` and `PersistentAgentRegistry` key every container by `agent://<path>`. There is no "per-unit agent host" container that multiplexes turns from sibling agents. `AgentHostingMode.Pooled` is reserved on the enum for the warm-pool work in [#362](https://github.com/cvoya-com/spring-voyage/issues/362) but is rejected at dispatch today.
- **Date:** 2026-04-22
- **Closes:** [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087)
- **Related code:** `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Dapr/Execution/PersistentAgentRegistry.cs`, `src/Cvoya.Spring.Dapr/Execution/EphemeralAgentRegistry.cs`, `src/Cvoya.Spring.Core/Execution/IAgentDefinitionProvider.cs` (`AgentHostingMode`).
- **Related docs:** [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md), [ADR 0012 — Extract container-runtime ownership into `spring-dispatcher`](0012-spring-dispatcher-service-extraction.md), [ADR 0017 — A Unit IS an Agent](0017-unit-is-an-agent-composite.md), [ADR 0011 — Persistent-agent lifecycle HTTP surface](0011-persistent-agent-lifecycle-http-surface.md), [ADR 0025 — Unified agent launch contract](0025-unified-agent-launch-contract.md).

## Context

A unit is a composite agent (ADR 0017). When the unified dispatch path landed (ADR 0025), the question of *what gets its own container* surfaced again. Two shapes were on the table:

1. **Per-agent.** Each leaf agent has its own container. A unit-of-three-agents is three containers.
2. **Per-unit (agent-host).** One long-lived container per unit; the unit multiplexes turns from member agents over a single A2A endpoint. The container exposes one A2A address and routes inbound `message/send` to whichever member agent the unit picked.

The agent-host shape is superficially attractive — fewer containers, smaller footprint, one image-pull per unit instead of per agent. It also matches the way a developer might informally think about "spinning up the engineering team": one process, one address.

Three pressure points pushed back against it:

1. **Different jobs.** A unit's job is to *route* — its runtime picks which child handles the turn (via the orchestration tools per [ADR-0039](0039-units-are-agents.md)), enforce boundary opacity, emit activity events. A leaf agent's job is to *invoke a tool* — Claude Code, Codex, Dapr Agent. Conflating them in a single container forces the routing logic and the tool invocation to share a runtime, a credential surface, and a process lifetime.
2. **Privilege boundary (ADR 0012).** The dispatcher is the single process that holds container-runtime credentials. Every other process — including the worker, the API host, and the agent containers themselves — runs without container-launch rights. A "per-unit agent host" container that multiplexes member agents would need to launch sibling tool processes (one per agent) inside itself, or it would need to call back into the dispatcher to launch them. The first option re-introduces nested container runtimes inside the agent host (rejected once already in #483). The second is two HTTP hops per turn for no observable gain.
3. **Cancellation, isolation, and credential blast radius.** A per-unit container that handles three agents' turns shares one process tree. A `tasks/cancel` for agent A would need to surgically signal only the spawned tool process for A, not the agent-host PID 1. A leaked credential reaches every agent in the unit. A pinned image reaches every agent in the unit. Per-agent scope keeps the blast radius equal to one agent.

## Decision

**Containers are scoped per agent. The dispatcher launches one container per `agent://<path>` and does not multiplex turns from sibling agents over a single container.** A unit's runtime picks a child via the orchestration tools; that child's dispatch goes to *its own* container. There is no per-unit container.

- **`AgentHostingMode.Ephemeral`** — one container per turn, scoped to one agent. Released on turn drain.
- **`AgentHostingMode.Persistent`** — one long-lived container per agent. `PersistentAgentRegistry` keys entries by agent id and forbids cross-agent reuse.
- **`AgentHostingMode.Pooled`** — reserved for [#362](https://github.com/cvoya-com/spring-voyage/issues/362). When implemented, the pool is *per agent definition* (image + tool + provider + model), not per unit. Two agents in the same unit with the same definition can share a pool slot; two agents with the same definition in different units can also share. Sharing keys off the launch fingerprint, not the unit address.

The dispatcher rejects `Pooled` with `NotSupportedException` today (verified by `A2AExecutionDispatcherTests.DispatchAsync_PooledHosting_ThrowsNotSupported`). The enum value exists so YAML written against #362 doesn't break the agent provider's parser before the implementation lands.

## Alternatives considered

- **Per-unit agent-host container.** One container per unit, multiplexes member agents over one A2A endpoint, owns the routing decision and the per-member tool invocations. **Rejected.** Conflates routing with tool invocation (different jobs), and either re-introduces nested container runtimes inside the agent-host (breaks ADR 0012's privilege boundary) or duplicates dispatcher hops (two HTTP calls per turn). Cancellation and credential isolation also become per-unit instead of per-agent.
- **One container per (unit, tool) pair.** Half-way: one container per distinct tool used in a unit, multiplexes agents that share the tool. **Rejected.** Same problems as per-unit, plus a sharing rule that's invisible to operators (two agents in the same unit happen to share a container if they pick the same tool, but not if they pick different tools). The rule would have to be re-discovered on every cost / isolation review.
- **One container per leaf agent, ever — including for ephemeral dispatch.** Pin every agent to one persistent container regardless of `AgentHostingMode`. **Rejected.** Defeats the point of `Ephemeral`: turn-shaped workloads should not pay persistent-agent cost. The persistent registry exists for agents that genuinely need warm state (resume tokens, pre-loaded credentials, large models); forcing every agent into it would erase the choice.

## Consequences

### Gains

- **The two jobs stay separate.** Routing lives in the unit actor (`UnitActor.OnMessageAsync`); tool invocation lives in the agent's container (via `AgentActor` → `A2AExecutionDispatcher`). Each is small enough to test in isolation.
- **Privilege boundary holds (ADR 0012).** The dispatcher is the only process with container-launch rights. The agent container has none — and because it's per-agent, it never needs to launch siblings.
- **Cancellation, credentials, and pin choices are per-agent.** A `tasks/cancel` targets one agent's container. A leaked credential reaches one agent. A pinned image reaches one agent. The blast radius is bounded by the agent boundary.
- **`Pooled` is a future registry change, not a future dispatcher change.** When #362 lands, the seam is `EphemeralAgentRegistry` (becomes pool-aware); the dispatcher path is unchanged.

### Costs

- **More containers in dense unit topologies.** A unit of N agents is N containers (or N pool slots). For OSS single-host deployments this is bounded by host capacity; for cloud deployments it's bounded by pod-density planning. Mitigated by `Ephemeral` (containers exist only during a turn) and by the future `Pooled` mode (warm slots shared across agents with the same launch fingerprint).
- **Image pulls are per agent definition, not per unit.** Two agents with different images both pull. The dispatcher's image-pull cache (Stage 2 of #522) deduplicates by image reference, so identical images pull once per host; this is a per-host efficiency, not a per-unit one.

### Known follow-ups

- **[#362](https://github.com/cvoya-com/spring-voyage/issues/362)** — implement `AgentHostingMode.Pooled`. Pool key is the launch fingerprint (image + tool + provider + model + workspace digest), not the unit address.

## Revisit criteria

Revisit if any of the below hold:

- A unit-of-N-agents topology becomes the operational norm (e.g. mean N > 10) **and** image-pull cost dominates dispatch latency. At that point reconsider whether sibling agents with identical launch fingerprints should share a container by default. The answer might be "extend `Pooled` to share across agents", not "reintroduce per-unit hosts".
- A new runtime image needs in-process coordination between sibling agents (shared state across turns inside one container). At that point the right move is probably a different abstraction (a shared cache, a unit-scoped state store), not a per-unit container.
- A future deployment topology (Kubernetes, edge) makes per-agent container scope structurally expensive — e.g. cold-start cost on a serverless platform would dominate. At that point the conversation is about pooling and warm-pool sizing, not about collapsing scope.
