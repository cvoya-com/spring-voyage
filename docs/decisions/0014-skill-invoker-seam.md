# 0014 — `ISkillInvoker` as the protocol-agnostic seam between skill callers and message routing

- **Status:** Accepted — `ISkillInvoker` is the single abstraction every skill caller (planners, the MCP server, future A2A gateway) resolves. The default implementation, `MessageRouterSkillInvoker`, translates a `SkillInvocation` into a `MessageType.Domain` `Message` and routes it through `IMessageRouter` so the whole platform governance chain (boundary, permissions, policy, activity) runs end-to-end. Callers never see `Message`.
- **Date:** 2026-04-17
- **Closes:** [#540](https://github.com/cvoya-com/spring-voyage/issues/540)
- **Implemented by:** [PR #541](https://github.com/cvoya-com/spring-voyage/pull/541) (closes [#359](https://github.com/cvoya-com/spring-voyage/issues/359))
- **Related:** [ADR 0008](0008-unit-boundary-decorator.md) (boundary opacity / projection — the visibility rules the invoker re-checks), [#413](https://github.com/cvoya-com/spring-voyage/issues/413) / [#497](https://github.com/cvoya-com/spring-voyage/issues/497) (boundary behaviour on the skill surface), [ADR 0013](0013-hierarchy-aware-permission-resolution.md) (the permission walk the router applies on the routed `Message`), [#487](https://github.com/cvoya-com/spring-voyage/issues/487) / [#498](https://github.com/cvoya-com/spring-voyage/issues/498) (expertise directory the skill surface is projected from), [#539](https://github.com/cvoya-com/spring-voyage/issues/539) (future A2A gateway — the first alternative invoker implementation), closed [#532](https://github.com/cvoya-com/spring-voyage/issues/532) (superseded first-pass implementation).
- **Related code:** `src/Cvoya.Spring.Core/Skills/ISkillInvoker.cs`, `src/Cvoya.Spring.Core/Skills/IExpertiseSkillCatalog.cs`, `src/Cvoya.Spring.Dapr/Skills/MessageRouterSkillInvoker.cs`, `src/Cvoya.Spring.Dapr/Skills/ExpertiseSkillRegistry.cs`, `docs/architecture/agent-runtime.md` § *Skill registries*.

## Context

PR #541 reworked the agents-as-skills surface to project skills **from the expertise directory** (#487 / #498) rather than from the agent roster. That pivot (from closed #532's agent-keyed `agent_{path}` snapshot-based shape) fixed two things: the skill surface reflects capability evolution live, and it honours unit boundary projection (#497) for free.

The rework added a new abstraction — `ISkillInvoker` — that every skill caller resolves instead of reaching for `IMessageRouter` directly. #541 shipped the seam inline but deferred the ADR to avoid numbering collisions across parallel in-flight PRs (principle 11 in `CONVENTIONS.md`). #539 (the A2A message gateway) is the first planned alternative implementation; documenting the contract now keeps that work unblocked.

Design questions the ADR must answer:

1. **Why a new abstraction at all?** `IMessageRouter` already exists. Why not let skill callers build `Message` directly?
2. **Why is the skill surface derived from the expertise directory, not the agent roster?** Closed #532 did the latter; we pivoted.
3. **How does a caller know an expertise entry is skill-callable vs consultative-only?**
4. **What is the name-space?** Can agent names leak into skill names?
5. **Snapshot or live resolution?** The original #532 took a startup snapshot; the rework does not.
6. **How does the seam compose with boundary opacity (ADR 0008) and permissions (ADR 0013)?**

## Decision

**Introduce `ISkillInvoker` as the single caller-facing abstraction. Its contract is protocol-agnostic: `SkillInvocation` in (skill name, JSON args, optional caller / correlation), `SkillInvocationResult` out (success payload or machine-readable error). `Message` never leaks across the seam. The default `MessageRouterSkillInvoker` routes every invocation through `IMessageRouter`; alternative implementations (starting with #539) register via `TryAdd` without touching callers.**

### Contract shape

```csharp
public interface ISkillInvoker
{
    Task<SkillInvocationResult> InvokeAsync(
        SkillInvocation invocation,
        CancellationToken cancellationToken = default);
}
```

`SkillInvocation` carries the skill name, the JSON argument payload, and an optional caller identity + correlation id. `SkillInvocationResult` is a discriminated shape: success with a payload, or a machine-readable error (e.g. `SKILL_NOT_FOUND`, `SKILL_INVOCATION_FAILED`). Nothing in the public contract mentions `Message`, `Address`, or `IMessageRouter` — those are implementation details of the default invoker.

### Why not let callers build `Message` directly

`IMessageRouter` is the enforcement seam for boundary opacity (#413 / #497 / ADR 0008), hierarchy-aware permissions (ADR 0013 / #414), cloning policy (#416), initiative levels (#415), and activity emission (#391 / #484). Callers that wanted to invoke a skill would otherwise each need to know how to build a `Message` with the right envelope, correlation, and target — and every new caller would be a governance hole if it forgot a hop. Funnelling through `ISkillInvoker` centralises envelope construction and the pre-routing boundary re-check (see below).

### Skill surface is projected from the expertise directory

The skill surface is a projection of `IExpertiseAggregator`, not of the agent roster. Live-resolved; no startup snapshot. Key consequences:

- **Source of truth is the directory.** Mutations (agent gains expertise, unit projection changes, boundary opacity flips) propagate on the next enumeration. There is no parallel capability registry to keep in sync.
- **Typed-contract eligibility.** `ExpertiseDomain.InputSchemaJson` is a nullable opt-in: non-null → skill-callable, null → consultative-only. Consultative-only entries stay message-only (they have no structured request contract). The schema field crosses the Dapr actor remoting boundary under `DataContractSerializer`, so it is stored as a `string?` (raw JSON text) rather than `JsonElement`.
- **Naming scheme: `expertise/{slug}`.** The slug is a case-folded, path-safe projection of the domain name (e.g. `python/fastapi` → `expertise/python-fastapi`; see `ExpertiseSkillNaming`). Agent names never appear in the skill surface: swapping the agent that holds an expertise entry does not rename the skill. Unit-projected capabilities and leaf-agent capabilities are addressed the same way.

### Boundary interaction (ADR 0008 / #497)

- Unit-projected expertise (origin = `unit://…`) is externally callable.
- Agent-level expertise inside a unit, not unit-projected, is visible only to inside callers.
- The catalog asks the aggregator for the caller-aware view, then filters non-unit origins out of external enumerations as defence in depth.
- **Invocation-time boundary re-check.** `MessageRouterSkillInvoker` resolves the skill against the caller's `BoundaryViewContext` before building the `Message`. A caller that knows the name of a hidden skill collapses to `SKILL_NOT_FOUND` without the router ever being called. The router then applies the full permission / policy chain on the routed `Message` — defence in depth.

### Live-resolution contract

No startup snapshot. `IExpertiseSkillCatalog.EnumerateAsync` (and the `GetToolDefinitions()` adapter on `ExpertiseSkillRegistry`) hits the aggregator on every call. The aggregator's cache + `InvalidateAsync` contract (ADR 0006) handles freshness; directory mutations propagate on the next enumeration.

### Staging plan for #539

#539 (the A2A message gateway) registers an alternative `ISkillInvoker` that translates a `SkillInvocation` into an outbound A2A call instead of an internal `Message` route. Callers do not change; operators opt in by registering the gateway implementation ahead of `MessageRouterSkillInvoker`. The default invoker is registered with `TryAdd*` so a downstream host (private cloud, integration test harness) can pre-register its own and keep it.

## Alternatives considered

- **Let callers build `Message` directly.** Simplest, but every caller has to re-derive envelope shape and re-apply boundary checks; every new caller is a potential governance hole. Rejected.
- **Keep `ISkillRegistry` as the seam; no separate invoker.** `ISkillRegistry` enumerates; putting invocation on the same interface conflates catalog reads with dispatch and makes the #539 swap harder (the gateway only replaces *invocation*, not the catalog). Rejected.
- **Agent-keyed skill surface (as in closed #532).** `agent_{path}` names tied to the agent roster. Simpler to implement but throws away the expertise-directory signal, does not reflect capability evolution without a restart, and forces a rename any time an expertise entry moves between agents. Pivoted away in #541.
- **Bake boundary re-check into `IMessageRouter` only.** Simpler call site but loses the fast-fail that collapses hidden-skill invocations to `SKILL_NOT_FOUND` without touching the router. Rejected: defence in depth at invocation time is cheap and valuable.

## Consequences

- **`ISkillInvoker` is the one place a future protocol slots in.** #539 adds an A2A-gateway implementation; callers (MCP server, planners, any future caller) do not change.
- **The expertise directory is the single source of truth for what can be called.** A capability gain on any member propagates on the next enumeration.
- **Agent churn does not reshape the skill surface.** Moving an expertise entry between agents does not rename the skill; callers that pinned a name keep working.
- **Boundary opacity is enforced twice on a skill call.** Once at catalog-resolve time via the caller's `BoundaryViewContext`, once inside the router on the built `Message`. Both must pass; either can veto.
- **`MessageRouter` stays the single enforcement seam for governance.** Permissions (ADR 0013), policies, initiative, cloning, and activity emission continue to run exactly once per call — the invoker just shaped the call.
- **Default registration is swap-friendly.** `MessageRouterSkillInvoker` is registered with `TryAdd*`; `ExpertiseSkillRegistry` is registered with `TryAddEnumerable` so the downstream private cloud repo can replace either without a fork.
- **Consultative expertise stays message-only.** A domain with no `InputSchemaJson` is invisible to the skill surface; it remains addressable through the normal message path. This preserves the free-form advisory channel without forcing a schema on it.
