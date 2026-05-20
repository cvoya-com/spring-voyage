# 0039 — Units are agents (orchestration is runtime behaviour, not platform configuration)

> **Amendment (2026-05-19, [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)) — orchestration is messaging, no separate gates:** the platform-side orchestration surface is not a separate message-sending mechanism with its own attachment or authorisation policy on top of ordinary messaging. The following changes apply to §3:
>
> 1. **§3.3 gate 2 ("Caller is a unit (`unit://` scheme)") is removed.** Agents may call orchestration tools; the membership / self-delegation / depth / tenant gates handle the actual safety properties. The SDK / dispatcher no longer mints or maps an `OrchestrationCallerIsNotUnit` reject code.
> 2. **The membership-based toolset attachment gate is removed.** The §3 "Tools attached only when children exist" rule is superseded: the launcher unconditionally attaches the closed five-tool set for every `agent://` and `unit://` address. `DirectoryOrchestrationToolProvider` no longer reads the membership graph; `IUnitMemberGraphStore` is no longer a dependency of that provider.
> 3. **§3.3 gate 3 ("Target is a direct child") is removed.** A caller may target any addressable entity in the same tenant — peer, sibling, parent, or member. The `OrchestrationTargetNotChild` reject code (and the dispatcher's 404 / SDK `TargetNotChild` exception mapping) is dropped. The §3.3 "live 'does this address have children right now?' check instead of scheme" alternative is therefore moot for attachment purposes and stays Rejected.
> 4. **Tool wire names drop the "child" / "children" framing.** New names per [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536): `list_members`, `inspect`, `delegate_to`, `fanout_to`, `query_status`. The C# enum members are `ListMembers`, `Inspect`, `DelegateTo`, `FanoutTo`, `QueryStatus`. `list_members` returns the caller's own direct members (empty for leaf agents); the other four tools accept any addressable target. C# types `OrchestrationChildDescriptor` / `ChildStatusResult` rename to `OrchestrationMemberDescriptor` / `MemberStatusResult`; embedded JSON schemas rename to match.
>
> The body below preserves the original §3 / §3.3 gate-2 / gate-3 / membership-attachment prose with strike-through for history.

- **Status:** Proposed — 2026-05-07 — A unit is an agent: it shares the agent's mailbox, execution configuration, and runtime; the only structural difference is that a unit has children. The platform stops modelling orchestration policy as a unit-level configuration concept; `IOrchestrationStrategy`, `AiOrchestrationStrategy`, `LabelRoutedOrchestrationStrategy`, `WorkflowOrchestrationStrategy`, `LabelRoutingPolicy`, the orchestration-strategy store, and the orchestration HTTP endpoints are removed. Where a unit has children, the runtime launcher attaches a fixed set of **orchestration tools** to the container (list children, inspect child, delegate to child, fan out, ask child status); the agent's instructions decide whether and how to use them. Orchestration **decisions** become first-class evidence — a structured `OrchestrationDecision` event recorded every time a unit's runtime invokes a delegation tool. Inheritance semantics generalise: top-level agents are tenant-parented like top-level units; multi-parent agents must define their own execution configuration unless every selected parent resolves identical config; reparenting revalidates the same rule. Container runtime selection (podman vs docker) is removed from operator-facing surfaces; it is platform configuration. Clean-deploy hard rename — no shim, no transitional flag.
- **Date:** 2026-05-07
- **Umbrella:** [#1786](https://github.com/cvoya-com/spring-voyage/issues/1786) — Design: unit-as-agent vs unit-as-router — orchestration vs execution boundary. Concrete bug that surfaced this: [#1759](https://github.com/cvoya-com/spring-voyage/issues/1759).
- **Related code:** see "Surface affected" — touches Core (`Orchestration/*`, `Policies/LabelRoutingPolicy`), Dapr (`Orchestration/*`, `Actors/UnitActor`, `Actors/AgentActor`), Host.Api (`Endpoints/OrchestrationEndpoints`), Connectors (GitHub label roundtrip), Manifest, Web, CLI, Dapr components, docs.
- **Related ADRs:** [0021](0021-spring-voyage-is-not-an-agent-runtime.md) (platform is not a runtime), [0024](0024-unit-validation-as-dapr-workflow.md) (unit lifecycle), [0025](0025-unified-agent-launch-contract.md) (launcher contract), [0029](0029-tenant-execution-boundary.md) (execution boundary), [0036](0036-single-identity-model.md) (identity), [0037](0037-package-schema-decomposition.md) (package shape), [0038](0038-agent-runtime-and-model-provider-split.md) (runtime / provider / model — the configuration shape this ADR layers on top of).

## Context

`IUnitActor : IAgent` is the existing code's compromise: a unit is an agent on the mailbox dimension (it receives and responds to messages) but has additional structure layered on top — members, permissions, lifecycle, connector binding, expertise aggregation, boundary, and a configured `IOrchestrationStrategy` that routes incoming messages to a chosen member. The existing comment on `IUnitActor` already states the principle ("a unit is an agent"), but the code only realises half of it. The other half — that a unit's *response* should come from the same runtime that powers any other agent — has never been wired up.

The gap surfaced concretely as [#1759](https://github.com/cvoya-com/spring-voyage/issues/1759). An operator created a unit, configured `(runtime: spring-voyage, model: ollama/llama3.2:3b)` per ADR-0038, sent it a message, and watched the message disappear. `AiOrchestrationStrategy.OrchestrateAsync` received it, found `context.Members.Count == 0`, returned `null`, and dropped the turn. The unit's own execution config — every field set, every credential resolved, validation passed — was never consulted.

That is not a one-off bug. It is a structural fault line:

- **Execution config** (`runtime`, `model`, `image`, `hosting` per ADR-0038) lives on the unit, is validated at unit-create time, and is read at dispatch time **only when there is a member to dispatch to**. Without a member, the config is dead weight.
- **Orchestration strategy** is keyed by a separate dimension (`AiOrchestrationStrategy`, `LabelRoutedOrchestrationStrategy`, `WorkflowOrchestrationStrategy`), persisted in `DbOrchestrationStrategyProvider`, surfaced through `OrchestrationEndpoints`, and consulted during dispatch — but never *reads* the unit's own runtime/model.

Two configurations on the same entity that must be compatible at dispatch time, with no coupling at the type level. ADR-0038 just split (runtime, model) cleanly, made provider intrinsic to the model, and removed `Kind`. The natural next step is: **stop modelling orchestration policy as a separate axis at all.**

The thread that produced this ADR ran through three explicit alternatives in #1786 (Option A: extend `AiOrchestrationStrategy`; Option B: dedicated self-executing strategy; Option C: synthetic member agent) and one synthesis (two-strategy split — `AiRoutedStrategy` + `AgentRuntimeStrategy`). All four kept `IOrchestrationStrategy` as a platform abstraction. None paid for the complexity. The reframing — *a unit is an agent; orchestration is something the agent's runtime does* — collapses the four options into one: the unit's runtime runs, the unit's runtime decides, the unit's runtime answers (or delegates).

A second piece arrived from the same review: orchestration is *evidence*, not policy. When a unit's runtime decides to delegate to a child, that decision is an artefact the platform records — same way the platform records other inter-actor decisions. We already half-have this (`ActivityEventType.DecisionMade` with `decision = "LabelRouted"` per `LabelRoutedOrchestrationStrategy`), but it is wired to a single strategy class. Generalising it gives the platform a uniform record of orchestration without owning orchestration policy.

This ADR records the resulting architecture.

## Decision

### 1. A unit is an agent

A unit is an entity that

- has an address (`unit://<id>`),
- has a mailbox (`IAgent.ReceiveAsync`),
- has execution configuration (`(runtime, model, image, hosting)` per ADR-0038),
- has children (zero or more agents or units).

A leaf agent is an entity that

- has an address (`agent://<id>`),
- has a mailbox (`IAgent.ReceiveAsync`),
- has execution configuration,
- does not have children.

The only structural difference is the children list. `IAgent` is the primary contract for both. `IUnitActor` and `IAgentActor` remain as convenience groupings for kind-specific affordances (a unit's permissions, expertise aggregation, boundary, lifecycle workflow; an agent's parent-unit pointer, skill list, expertise, thread management) but neither is a separate first-class concept on the dispatch dimension.

The address scheme distinction (`unit://` vs `agent://`) stays for routing and identity continuity; it does not gate behaviour.

Rejected: collapse the address schemes into a single `agent://` namespace. The schemes encode containment shape (units have children, leaf agents do not) and are referenced across the directory, manifests, OpenAPI, and CLI. Re-keying every persisted address is a v0.2 conversation, not a v0.1 cleanup; it is also unnecessary for this ADR to deliver its value.

Rejected: collapse `IUnitActor` and `IAgentActor` into one C# interface. The unit-only methods (lifecycle workflow, permissions, expertise aggregation, boundary, connector binding, permission inheritance) are real domain surface and have no analogue on a leaf agent; an agent-only method set (skills, thread management, parent-unit pointer) is similarly real. Two interfaces with a shared `IAgent` parent expresses this cleanly.

### 2. Orchestration is runtime behaviour, not platform configuration

The platform does not own an orchestration-policy abstraction. There is no `IOrchestrationStrategy`, no concrete strategy classes, no orchestration store, no orchestration endpoints. The following are removed in full:

| Type | Path | Disposition |
|---|---|---|
| `IOrchestrationStrategy` | `Cvoya.Spring.Core/Orchestration/IOrchestrationStrategy.cs` | **Deleted** |
| `IOrchestrationStrategyProvider` | `Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyProvider.cs` | **Deleted** |
| `IOrchestrationStrategyResolver` | `Cvoya.Spring.Core/Orchestration/IOrchestrationStrategyResolver.cs` | **Deleted** |
| `IUnitOrchestrationStore` | `Cvoya.Spring.Core/Orchestration/IUnitOrchestrationStore.cs` | **Deleted** |
| `IOrchestrationStrategyCacheInvalidator` + `NullOrchestrationStrategyCacheInvalidator` | `Cvoya.Spring.Core/Orchestration/` | **Deleted** |
| `AiOrchestrationStrategy` | `Cvoya.Spring.Dapr/Orchestration/AiOrchestrationStrategy.cs` | **Deleted** |
| `LabelRoutedOrchestrationStrategy` | `Cvoya.Spring.Dapr/Orchestration/LabelRoutedOrchestrationStrategy.cs` | **Deleted** |
| `WorkflowOrchestrationStrategy` | `Cvoya.Spring.Dapr/Orchestration/WorkflowOrchestrationStrategy.cs` | **Deleted** |
| `CachingOrchestrationStrategyProvider`, `DbOrchestrationStrategyProvider`, `DefaultOrchestrationStrategyResolver`, `DbUnitOrchestrationStore` | `Cvoya.Spring.Dapr/Orchestration/` | **Deleted** |
| `LabelRoutingPolicy` | `Cvoya.Spring.Core/Policies/LabelRoutingPolicy.cs` | **Deleted** (functionally replaced by runtime instructions + the GitHub connector's roundtrip subscriber observing `OrchestrationDecision` events — see decision 4 and decision 7) |
| `OrchestrationEndpoints` + `OrchestrationModels` | `Cvoya.Spring.Host.Api/Endpoints/`, `Cvoya.Spring.Host.Api/Models/` | **Deleted** |
| `orchestration:` block (and per-strategy subblocks) on the unit manifest | `Cvoya.Spring.Manifest/UnitManifest.cs` | **Deleted** |
| `IUnitActor`'s strategy-delegation path in `ReceiveAsync` | `Cvoya.Spring.Dapr/Actors/UnitActor.cs` | **Replaced** (the unit's `ReceiveAsync` invokes the same runtime-launcher path that `AgentActor.ReceiveAsync` invokes; see decision 5) |

There is no equivalent platform abstraction introduced to take their place. Orchestration is what an agent does when it has children and chooses to delegate; the choice and the mechanics live inside the runtime image. The platform's concern is to (a) surface children as callable tools to the runtime (decision 3), (b) record the runtime's delegation decisions as evidence (decision 4), and (c) deliver the resulting message to the chosen child (existing routing, unchanged).

Rejected: keep `IOrchestrationStrategy` as a platform interface and let runtimes provide implementations. The interface adds a layer for which there is no second consumer — every strategy is implemented and consumed by the platform itself, and the runtime/launcher path already exists for agent execution. Two parallel dispatch surfaces is the duplication this ADR removes.

Rejected: replace the strategy taxonomy with a single `IUnitDispatcher` slot whose default routes to the unit's runtime. Same shape as the previous decision under a different name; same duplication.

### 3. Children are exposed as orchestration tools to the runtime

When a unit's runtime is launched for a turn, the launcher attaches a fixed set of orchestration tools to the container in addition to whatever skills the unit's instructions configure. The tool surface is uniform across all runtimes that support tool calling (`spring-voyage`, `claude-code`, `codex`, `gemini`); a custom runtime that does not support tool calls can still respond directly but cannot delegate.

The v0.1 orchestration-tool surface (wire names per the 2026-05-19 amendment, [#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)):

| Tool name | Purpose | Returns | Side effect |
|---|---|---|---|
| `list_members` | Enumerate the caller's own direct members with their addresses, display names, kinds (`agent` / `unit`), and (resolved) execution config. Returns an empty array for leaf agents. | Array of member descriptors. | None — emits an `OrchestrationDecision` with `kind: inspect` only when explicitly invoked by the runtime as part of a decision sequence; a bare list does not record. |
| `inspect <address>` | Return metadata for any addressable target in the caller's tenant: role, description, declared expertise, current status. | Single descriptor. | None. |
| `delegate_to <address> <message>` | Forward the inbound message to the named target and await the target's response (synchronous within the turn budget). | The target's response message. | Records an `OrchestrationDecision` with `kind: delegate`. |
| `fanout_to <addresses[]> <message>` | Forward to multiple targets in parallel; collect responses with a per-target timeout. | Array of `(address, response, status)` triples. | Records an `OrchestrationDecision` with `kind: fanout`. |
| `query_status <address>` | Cheap status check for a target without a full inspect. | `{ status, lastActivityAt, busyOnThread? }`. | None. |

The tool surface is **closed for v0.1**. New tools require an ADR. The reasoning: the tool surface is a contract every runtime image in the catalogue implements; widening it implicitly forces every image to keep up. The closed list also bounds the platform-side behaviour the platform *records* (via decision 4) — if a runtime calls a tool the platform does not enumerate, the platform has nowhere to put the evidence.

~~Tools are attached to the runtime *only* when the addressed entity has at least one child in the member graph (`Members.Count >= 1` in today's terminology). Per the 2026-05-19 amendment at the top of this ADR, the gate is purely membership-based; entity type (agent vs unit) is not consulted. An entity with zero children — whether `agent://` or `unit://` — is launched with no orchestration tools, and the absence of the tools makes "no children to delegate to" visible to the runtime's instructions without an ambient flag.~~ *Superseded by the 2026-05-19 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)): the membership-based attachment gate is removed. The launcher unconditionally attaches the closed five-tool set for every `agent://` and `unit://` address; `list_members` returns the empty array for leaf agents.*

The mechanism by which tools are presented to the runtime is runtime-specific (MCP server, in-process tool registry, env-var-keyed tool list, or whatever the runtime image expects). The launcher contract from ADR-0025 already covers tool injection; this ADR adds **orchestration tools** as a category alongside skills.

```csharp
// Cvoya.Spring.Core.Orchestration — only file in the namespace post-ADR.
// IAgentRuntimeLauncher gains a single new responsibility: when launching a
// container for an agent that has children, attach the orchestration tool set.
public interface IOrchestrationToolProvider
{
    /// <summary>
    /// Returns the orchestration tool descriptors the launcher should attach
    /// when invoking <paramref name="agent"/> in <paramref name="thread"/>.
    /// Returns an empty array for agents without children.
    /// </summary>
    OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, ThreadId thread);
}

public sealed record OrchestrationToolDescriptor(
    string Name,                 // Closed enum: list_members, inspect, delegate_to, fanout_to, query_status
    JsonElement InputSchema,     // JSON Schema for the runtime's tool-call surface
    JsonElement OutputSchema);
```

The launcher consults the provider at launch time. The platform implementation of the provider reads the directory and the runtime catalogue and produces the descriptor array; tests can substitute a stub. Closed enum for `Name` keeps the surface auditable and matches the runtime-side dispatch.

Rejected: a fully open tool registry where any platform component can register orchestration tools. Inverts the dependency direction (the platform would announce capabilities to the runtime instead of the runtime announcing capabilities to the platform) and turns a closed contract into an extension point with no second implementation.

Rejected: model children as resource records the runtime fetches from a generic platform API. The runtime image would need to bake in platform-specific HTTP-client logic; tools as a contract are simpler and runtime-agnostic.

Rejected: scope orchestration tools per child kind (different tools for `agent` children vs `unit` children). Children are agent-shaped on the mailbox dimension (decision 1); a uniform tool surface lets the unit's runtime treat them uniformly. If a future tool genuinely needs the kind, the descriptor returns it via `inspect`.

#### Two surfaces, one set of handlers

The orchestration tool set is exposed via **two parallel surfaces**, both dispatching to the same platform-side handlers (decision 5) and emitting the same `OrchestrationDecision` events (decision 4):

- **Tool-call surface for LLM-driven runtimes.** Per-runtime mechanism — MCP for `claude-code` / `codex` / `gemini`, env-var-keyed registry for `spring-voyage-agent`. The launcher attaches descriptors at launch time per the launcher contract from ADR-0025. The runtime's LLM invokes the tools through its tool-call surface; the runtime's instructions determine when to use them. This is the surface the table above describes.

- **SDK surface for workflow-driven runtimes.** An HTTP callback API exposed by the host (under the dispatcher per ADR-0029), with a typed client in a new `Cvoya.Spring.AgentSdk` package. Runtime image authors whose images run a static workflow (any technology — Temporal, Dapr Workflows, custom state machines, plain code) consume the SDK to call the same orchestration tools as method calls. The agent's role in this shape is to invoke and manage the workflow; **workflow state is the developer's concern**, not the platform's.

Both surfaces accept the same arguments, return the same shapes, dispatch to the same handlers, and emit the same `OrchestrationDecision` events. The runtime image author chooses which surface fits the image's implementation; the platform does not care.

The SDK shape (sketch — final names land in the implementation):

```csharp
// Cvoya.Spring.AgentSdk — consumed by runtime image authors.
public interface IOrchestrationClient
{
    Task<ChildDescriptor[]> ListChildrenAsync(CancellationToken ct = default);
    Task<ChildDescriptor> InspectChildAsync(Address child, CancellationToken ct = default);
    Task<Message> DelegateToChildAsync(Address child, Message message, string? reason = null, CancellationToken ct = default);
    Task<FanoutResult[]> FanoutToChildrenAsync(Address[] children, Message message, string? reason = null, CancellationToken ct = default);
    Task<ChildStatus> QueryChildStatusAsync(Address child, CancellationToken ct = default);
}

public static class SpringAgent
{
    // Reads SPRING_CALLBACK_URL + SPRING_CALLBACK_TOKEN from the environment;
    // throws if either is missing or invalid.
    public static IOrchestrationClient FromEnvironment();
}
```

Authentication is **per-invocation**: the launcher mints a callback token with `(tenantId, agentAddress, threadId, messageId, expiresAt)` claims at launch time and injects it as `SPRING_CALLBACK_TOKEN` alongside the `SPRING_CALLBACK_URL` env var. The host validates the token on every callback. Tokens are scoped to one invocation and one runtime; they expire when the invocation ends.

Both env vars are written by the launcher uniformly across runtimes (`SpringVoyageAgentLauncher`, `ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`). LLM-driven runtimes that ignore the env vars (because the tool-call surface is sufficient) waste only the bytes of the env entry; workflow images that consume the SDK find the dispatcher and the token without per-runtime configuration.

#### Authorization rules

*Heading revised per the 2026-05-19 amendment — the original "the SDK is unit-callable only" framing is replaced; the gates below still apply except for gate 2.*

The dispatcher applies a strict authorization model on the SDK surface, layered on top of token validation. The same model applies to the LLM tool-call surface — the platform-side handlers (decision 5) enforce identical rules regardless of which transport reached them. Per the 2026-05-19 amendment, the platform does not gate orchestration by entity type; the membership / self-delegation / depth / tenancy gates handle the authorisation properties.

The dispatcher applies these gates in order:

1. **Token validation.** The signature, expiry, and `(tenantId, agentAddress, threadId, messageId)` claim shape are validated as the first gate. Invalid → **401**.
2. ~~**Caller is a unit.** The caller's `agentAddress` claim must use the `unit://` scheme. An `agent://` claim is rejected with **403 `OrchestrationCallerIsNotUnit`** without a directory call — the address scheme is the structural property, no live lookup needed. A unit with zero children remains a unit and can call `list_children` to get an empty array; that case is allowed and returns 200 with `[]`.~~ *Superseded by the 2026-05-19 amendment at the top of this ADR: gate 2 is removed; agents may call orchestration tools and the membership gate (gate 3) handles the actual safety property.*
3. ~~**Target is a direct child.** For `delegate_to_child`, `fanout_to_children`, `inspect_child`, and `query_child_status`, the dispatcher resolves each target address against the caller's *current* direct children at call time. A non-child target → **404 `OrchestrationTargetNotChild`**. Direct children only — the caller cannot delegate across levels (cross-level access deferred to v0.2 if a real use case demands; tracked separately as a v0.2 issue under #1786). This rule also handles the membership-changed-mid-invocation race: the token was minted at one membership state, but the directory at call time is authoritative.~~ *Superseded by the 2026-05-19 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)): orchestration is messaging — a caller may target any addressable entity in the same tenant. The `OrchestrationTargetNotChild` reject code is removed and the dispatcher no longer reads the membership graph to authorise targets.*
4. **Self-delegation is rejected.** A target address that equals the caller → **400 `OrchestrationSelfDelegation`**. Without this gate, a unit calling itself would either stack-overflow the platform (synchronous case) or deadlock (asynchronous case). Trivial check; high value.
5. **Per-thread orchestration depth.** Each delegation (`delegate_to` or `fanout_to`) increments a thread-scoped counter; the platform rejects with **429 `OrchestrationDepthExceeded`** when the counter hits the v0.1 ceiling (default 8 nested delegations per thread per top-level inbound message; configurable per host). The counter is platform-managed thread state, scoped to the inbound message's transitive call chain. The depth budget is a coarse loop-prevention mechanism; precise cycle detection (e.g., reject when the current caller appears upstream in the chain) is deferred to v0.2.
6. **Cross-tenant containment.** The token's `tenantId` claim must match the caller's tenant on every call; mismatch is **403**. ADR-0029's tenant boundary stays uncrossable through this surface.

~~This model means the SDK is **structurally unit-callable only**. A leaf-agent image can invoke `IOrchestrationClient` method calls; every method returns `OrchestrationAuthException(Reason = CallerIsNotUnit)`. The SDK's per-method documentation calls this out so image authors do not write code that quietly fails for leaf-agent images.~~ *Superseded by the 2026-05-19 amendment: the platform does not gate orchestration by entity type. Calls succeed whenever the caller's direct-child membership permits — agents with children may orchestrate, units with no children may not.*

**Delegation creates the target's own thread context, not membership in the caller's thread.** When `unit_a` is invoked for thread `{human, unit_a}` and calls `delegate_to(agent_b, message)`, the target does **not** need prior membership in the inbound thread. Per ADR-0030's participant-set keying, the delegation creates (or resumes) the target's own thread `{unit_a, agent_b}`; the target sees its own threadId and its own conversation state with the caller. The original `{human, unit_a}` thread continues unchanged. This is fundamental to v0.1 — without it, delegation does not work.

**What is *not* in scope for v0.1.** A unit's runtime, *while invoked for thread T1*, calling SDK methods that act on a different thread T2 (e.g., posting messages into a thread the runtime was not invoked for). The token's `threadId` claim scopes every call to one thread; the SDK does not expose multi-thread interaction. No legitimate use case has surfaced; v0.2 may revisit if one does.

**What leaf agents (and units) keep.** Inter-agent messaging — sending a message to a peer, replying to a thread, addressing another unit — is **not** an orchestration concern. It is the existing Agent-to-Agent (A2A) protocol surface (see `Cvoya.Spring.A2A`), gated by the existing membership graph and per-thread permission model. Leaf agents can message any addressable peer for which they hold the required permissions; the orchestration SDK does not gate, replace, or shadow that path. The SDK is a *strict subset* of the platform's runtime-callback surface, scoped to the single thing only units do: orchestrate their direct children.

| Capability | Any caller (`agent://` or `unit://`) |
|---|---|
| Send a message to a peer (A2A) | yes (existing A2A protocol, permission-gated) |
| Reply to the inbound message | yes (existing runtime-output channel) |
| Call orchestration SDK (`list_members`, `delegate_to`, …) | yes — entity type is not gated, membership is not gated |
| `delegate_to` any addressable target in the same tenant | yes |
| `delegate_to` self | **no — 400 `OrchestrationSelfDelegation`** |
| Exceed per-thread orchestration depth | **no — 429 `OrchestrationDepthExceeded`** |
| Cross-tenant calls | **no — 403** |

*Table revised per the 2026-05-19 amendment. The original two-column "leaf agent vs unit" form is preserved in the ADR's git history.*

Rejected: skip the env-var injection on leaf-agent runtimes. The launcher would have to know the SDK surface's eligibility at launch time; over time, as more SDK surfaces are added (A2A from runtime, structured logging, configuration access), the eligibility check becomes a brittle list that the launcher has to keep in sync. Cleaner: inject uniformly, gate at the endpoint.

Rejected at first writing and ~~adopted~~ then re-rejected by the 2026-05-19 amendment ([#2536](https://github.com/cvoya-com/spring-voyage/issues/2536)): the live "does this address have children right now?" check was briefly the gate; the amendment removes it together with gate 3. Toolset attachment is now unconditional for `agent://` and `unit://` addresses; address scheme decides only "do we attach the orchestration MCP at all?" The original rationale ("a unit with zero members can call `list_members` and get `[]`") still holds — the call itself is allowed and returns the empty array.

~~Rejected: return 401 for non-unit calls (the token is "valid"). The token *is* valid; the caller is just not authorized for this surface. 403 with a precise error code is the conventional shape and lets the SDK throw a typed exception (`OrchestrationAuthException` with `Reason = CallerIsNotUnit`) that image authors can catch.~~ *Superseded by the 2026-05-19 amendment: there is no non-unit-caller gate, so the 401-vs-403 question no longer applies.*

Rejected: cycle detection on the address chain (instead of a depth counter). v0.1 ships the cheap mechanism; v0.2 may add proper cycle detection (the issue is filed at this ADR's merge time, sub-issue under #1786).

Rejected: expose orchestration tools only via the LLM tool-call surface. Forces non-LLM runtimes (workflow images, deterministic agents) to wrap an MCP client even when MCP adds nothing to their actual implementation. The SDK surface lets workflow authors write code that reads as code.

Rejected: bake a specific workflow technology into the platform. The platform cannot predict which technology image authors prefer (Temporal, Dapr Workflows, xstate, hand-rolled state machines, or simple imperative code), and forcing one limits the addressable runtime catalogue. The SDK exposes the orchestration primitives; the technology choice is the developer's.

Rejected: persist workflow state in the platform. Workflow durability is a runtime-author concern; the platform's job is to deliver messages and record decisions, not to be a workflow engine. ADR-0039 stays deliberately small on this axis. A runtime image that needs workflow durability ships its own state store (Postgres, SQLite, S3, Dapr workflow state — author's choice) inside the image's process boundary or as a sidecar.

Rejected: invent a separate `Cvoya.Spring.OrchestrationSdk` parallel to a future general-purpose `Cvoya.Spring.AgentSdk`. The orchestration surface is the *first* SDK consumer; subsequent runtime-author surfaces (read inbound message, write outbound message, read configuration, log structured events) belong in the same package. One SDK package keeps the dependency story for image authors clean.

### 4. Orchestration decisions are first-class evidence

When a unit's runtime invokes a delegation tool (`delegate_to`, `fanout_to`), the platform records an `OrchestrationDecision` event in the activity stream. The shape:

```csharp
public sealed record OrchestrationDecision(
    Guid DecisionId,
    Guid TenantId,
    Address UnitAddress,
    Guid ThreadId,
    Guid InputMessageId,
    OrchestrationDecisionKind Kind,
    Address[] Targets,
    OrchestrationDecisionStatus Status,
    Guid[] ResultMessageIds,
    string? Reason,
    JsonElement? Metadata,
    DateTimeOffset CreatedAt);

public enum OrchestrationDecisionKind { Delegate, Fanout, Inspect, NoOp }
public enum OrchestrationDecisionStatus { Accepted, Routed, Failed }
```

`Reason` is the runtime-supplied rationale **as text** — what the agent's tool call said in its `reason` argument. It is **never** the model's hidden chain-of-thought; runtimes that surface internal reasoning must redact it before the platform observes the tool call.

The event replaces and generalises today's `ActivityEventType.DecisionMade` with `decision = "LabelRouted"` payload from `LabelRoutedOrchestrationStrategy.PublishAssignmentEventAsync`. Specifically:

- The GitHub connector's label-roundtrip subscriber (`Cvoya.Spring.Connector.GitHub/Labels/LabelRoutingRoundtripSubscriber.cs`) is rewritten to subscribe to `OrchestrationDecision` events (filtered by `Kind == Delegate` and a per-tenant rule about which labels to apply on assign / remove) instead of to `LabelRoutingPolicy`-keyed activity events.
- The roundtrip rule (which labels to apply on a delegation) moves from `LabelRoutingPolicy.AddOnAssign` / `RemoveOnAssign` (a unit-level policy entity) to a connector-side configuration: the GitHub connector binding stores label-roundtrip rules per connector instance. This keeps connector mechanics on the connector side, not in core platform domain.

Decisions are recorded via the existing `IActivityEventBus`. The ADR introduces no new bus surface.

Rejected: store decisions in a dedicated relational table, separate from activity events. The activity stream is already the audit channel; a parallel store doubles read paths and the activity stream's existing query surface (per-unit, per-thread, time-bounded) is exactly what operators need. The schema is recorded as `Activity_OrchestrationDecision` rows with the typed payload deserialised from `Details`; one new schema, not a new pipeline.

Rejected: record only on `Failed` status. Successful delegations are the audit interest — operators want to know *who decided to delegate to whom*, not just when delegation broke.

Rejected: include the runtime's full tool-call payload (input + output) on the event. Runtime tool calls can carry arbitrary user content; recording it raises confidentiality concerns at scale. The `Reason` text the runtime supplies is the agreed audit detail; deeper provenance lives in turn-level traces (out of scope for this ADR).

### 5. The unit's mailbox runs the unit's runtime

`UnitActor.ReceiveAsync` no longer consults `IOrchestrationStrategyResolver`. It invokes the same runtime-launcher path that `AgentActor.ReceiveAsync` invokes:

1. Resolve the unit's effective execution config (own values + parent inheritance per decision 6).
2. Resolve the unit's effective skill set (own + inherited).
3. Resolve the unit's orchestration tools via `IOrchestrationToolProvider` (empty array if no children).
4. Resolve the unit's resolved credential row via the ADR-0038 `(tenant, provider, authMethod)` key with ADR-0003 unit-level fall-through.
5. Hand the bundle to `IAgentRuntimeLauncher`, which spawns the container per ADR-0025 / ADR-0026, attaches the bound thread per ADR-0038's `threadBinding`, and delivers the message.
6. Capture the runtime's response (or the runtime's tool calls + final response) and emit the message + any `OrchestrationDecision` events.

`AgentActor.ReceiveAsync` follows the same six-step path, except step 3 always returns an empty array. The two actors share the runtime invocation but differ in what they offer the runtime; the difference is data, not code.

The "runtime" the launcher invokes can be LLM-driven (image consumes the tool-call surface from decision 3) or workflow-driven (image consumes the SDK surface from decision 3). The mailbox does not branch on the runtime style — it launches, delivers, and captures. The same launcher contract serves both shapes; the same orchestration tools are reachable through both surfaces; the same `OrchestrationDecision` events emit regardless of which surface the image used to make the call.

This is the delete-side counterpart to decision 2: removing `IOrchestrationStrategy` is only sound if there is one place that runs the unit. That place is the runtime launcher. The actor's work is to *deliver* the inbound message and *publish* the outbound message; the *deciding* lives in the runtime.

Cost note (acknowledged, not solved here): every unit response is a runtime-launcher invocation, which is materially heavier than yesterday's `IAiProvider.CompleteAsync` round-trip in `AiOrchestrationStrategy`. Container reuse, warm pools, and per-thread session affinity already exist for agent-side runtimes; they apply uniformly to unit-side runtimes after this ADR. No new optimisation surface is required, and none is delivered.

### 6. Inheritance: top-level under tenant; multi-parent override; reparenting validation

Inheritance generalises the same rule across leaf agents and units:

**Top-level entities are tenant-parented.** A top-level unit (no parent unit) inherits its execution config from tenant defaults — already true. A top-level agent (no parent unit) is admitted by the platform and inherits from tenant defaults the same way. ADR-0003's secret-inheritance fall-through (unit → tenant) extends one level: leaf-agent → tenant when no unit parent exists. The CLI reflects this — `spring agent create --name <…>` without `--unit` is valid and produces a top-level tenant-parented agent. The portal's agent-create form treats an empty unit-multi-select as "tenant-parented."

**Single-parent agents inherit by default; an explicit override on any execution-config field is permitted.** This is the field-level inheritance pattern from DESIGN.md §12.6 (per-field placeholder + `Inherits` / `Configured` badge), already shipped on the unit-create wizard. The override is per-field; an agent can inherit `runtime` from its parent unit and override `model` to a different value.

**Multi-parent agents must define their own execution configuration unless every selected parent resolves identical effective config.** When an agent is a member of more than one unit and the operator leaves any execution field inherited:

1. The platform resolves each parent unit's effective execution config.
2. If every parent resolves the same `(runtime, model.provider, model.id, image, hosting)`, the agent inherits that config.
3. If any field differs across parents, the platform rejects the create / update with a precise error naming the diverging field and the conflicting parent values. The operator must either remove the conflicting parent or explicitly set the field on the agent.

The same rule runs on **reparenting**: a `PUT /units/{id}/memberships/{addr}` (or the new agent-multi-parent equivalent) that would put an inheriting agent under conflicting parents is rejected with the same error before the membership row is written. The validation is server-side; the CLI and the portal surface it as a pre-flight check but the platform is authoritative.

```csharp
// Cvoya.Spring.Core.Agents — new helper used by both the create endpoint
// and the membership-update endpoint.
public interface IExecutionConfigInheritanceResolver
{
    InheritanceResolution ResolveAgentConfig(
        AgentExecutionConfig agentOwn,
        IReadOnlyList<UnitId> parentUnitIds,
        TenantId tenantId,
        CancellationToken ct);
}

public sealed record InheritanceResolution(
    AgentExecutionConfig Effective,
    IReadOnlyDictionary<string, IReadOnlyList<ParentValue>> ConflictingFields);

public sealed record ParentValue(UnitId Source, string Value);
```

The resolver runs at:

- `POST /api/v1/tenant/agents` (create).
- `PUT /api/v1/tenant/agents/{id}/execution` (execution-config update).
- `POST /api/v1/units/{id}/agents/{agentId}` (assignment).
- `DELETE /api/v1/units/{id}/agents/{agentId}` (un-assignment — only relevant if the remaining parent set diverges).
- The same shape on unit-side (a unit can also be a member of another unit; multi-parent conflicts apply identically).

A 422 response with the structured `ConflictingFields` map lets the wizard surface the conflict precisely (per DESIGN.md §6.3 inline-error pattern); the CLI prints the same map.

Rejected: pick a parent at random when configs conflict. Hides operator decisions; produces non-deterministic dispatch behaviour.

Rejected: pick the parent with the highest readiness score (or any other priority rule). Same hiding problem with a more elaborate rule.

Rejected: validate at dispatch time, not at create / reparent time. Surface the conflict only when a message arrives and the agent fails to resolve a config — the symptom of #1759 reproduced one level up. The operator gets to know about the conflict at write time, when they have context to fix it.

### 7. Container runtime is platform configuration

`execution.containerRuntime` (`docker` | `podman`) is removed from operator-facing surfaces. The selection is platform configuration: the host process picks one runtime at deploy time, and every agent on that host uses it.

Removed:

- `--container-runtime` flag on `spring agent create` / `spring unit create` / any other CLI command that exposes it.
- Container-runtime selector on the unit-create wizard.
- `containerRuntime` field on the agent-create wizard's API surface.
- `containerRuntime` on the manifest's `execution:` block.
- `ContainerRuntime` field on `AgentExecutionConfig` / `UnitExecutionConfig` (or the equivalent post-ADR-0038 type names).

The host's runtime choice stays where it lives today — a host-level configuration value the operator sets when deploying the platform. ADR-0026 (per-agent container scope) is unaffected.

Rejected: keep the field as a tenant-level override (a tenant can pin podman even if the host defaults to docker). No platform consumer; the host's runtime binary is a platform fact, not a tenant fact.

### 8. CLI break: positional `<id>` removed from `spring agent create`

`spring agent create` accepts `--name` (display name) and no positional argument. The previous positional `<id>` (sent as the legacy `Name` field while the server allocates a Guid) is removed.

```
# Before
spring agent create my-agent --name "Ada" --unit eng

# After (ADR-0039)
spring agent create --name "Ada" --unit eng
```

The CLI rejects a positional argument with a parse-time message naming this ADR. v0.1 is allowed to take CLI breaking changes; the `--name` argument is the only display surface.

The same cleanup applies to `spring unit create` if it carries a positional `<id>` today (verify at implementation; if it does, remove it). One CLI shape across `spring agent create` / `spring unit create` reinforces decision 1: the two surfaces look the same because the underlying entity is the same shape.

Rejected: keep the positional argument with a deprecation warning. A v0.2-deferred deprecation when v0.1 is allowed to break is unnecessary process; the message at parse time already names the ADR.

### 9. Migration: clean-deploy hard rename, no shim

This ADR re-shapes:

- C# types: `IOrchestrationStrategy` and friends deleted (decision 2); `LabelRoutingPolicy` deleted; `IUnitOrchestrationStore` deleted; `OrchestrationEndpoints` deleted; `UnitActor.ReceiveAsync` rewritten; `IOrchestrationToolProvider` added; `OrchestrationDecision` event added; `IOrchestrationClient` added (in the new `Cvoya.Spring.AgentSdk` package — decision 3, surface 2).
- New project: `Cvoya.Spring.AgentSdk` — runtime-author-facing typed client for the orchestration SDK surface. Reads `SPRING_CALLBACK_URL` and `SPRING_CALLBACK_TOKEN` from the env and calls the dispatcher's orchestration callback API.
- Dispatcher (`Cvoya.Spring.Dispatcher`): new orchestration callback endpoints (one per tool name); per-invocation callback-token validation middleware; both reuse the platform-side handlers shared with the LLM tool-call surface.
- Launcher contract (per ADR-0025): launchers mint and inject `SPRING_CALLBACK_TOKEN` (signed JWT scoped to one invocation) and `SPRING_CALLBACK_URL` for every runtime, regardless of whether the runtime consumes them. LLM-only runtimes ignore the env vars; workflow runtimes consume them via the SDK.
- Manifest: `unit.orchestration:` block removed; `unit.execution.containerRuntime` removed.
- Wire DTOs: orchestration HTTP surface gone; multi-parent inheritance error responses added; container-runtime fields removed from create / update payloads.
- CLI: positional `<id>` on `spring agent create` removed; `--container-runtime` removed from agent-and-unit create surfaces.
- Web portal: orchestration-strategy step / picker on the unit-create wizard removed; container-runtime selector removed; multi-parent inheritance conflict UX added (DESIGN.md §6.3 inline-error reuse).
- GitHub connector: label-roundtrip subscriber rewritten against `OrchestrationDecision` events; per-binding rule replaces `LabelRoutingPolicy`.
- Sample: a workflow-driven runtime image demonstrating SDK usage end-to-end (proof that the SDK surface can replace the deleted `WorkflowOrchestrationStrategy`).
- Tests: every layer.
- Docs: `docs/architecture/units.md`, `docs/concepts/agents.md`, `docs/architecture/orchestration.md` (likely retired — moves to `docs/concepts/agents.md`); new `docs/architecture/agent-sdk.md` covering the SDK contract, env-var convention, and authoring guide.

There is no transitional flag, no dual-acceptance window, no shim. Old-shape signals surface as parse / API errors with precise migration hints:

| Old shape | Error | Migration hint |
|---|---|---|
| Unit manifest has `orchestration:` block | `LegacyUnitOrchestrationField` | "orchestration: is removed in ADR-0039; orchestration is decided by the unit's runtime, not by platform configuration." |
| HTTP request to `POST /api/v1/units/{id}/orchestration` | 410 Gone | "the orchestration endpoint is removed in ADR-0039; configure the unit's runtime instead." |
| Wire DTO carries `containerRuntime` on `execution:` | `LegacyContainerRuntimeField` | "containerRuntime is removed in ADR-0039; the container runtime is platform configuration." |
| CLI `spring agent create my-agent …` (positional) | parse-time rejection | "the positional argument is removed in ADR-0039; use --name." |
| Manifest carries `LabelRoutingPolicy` block | `LegacyLabelRoutingPolicy` | "label routing is configured on the connector binding in ADR-0039; see docs/concepts/connectors.md#github-label-routing." |

Operators with persisted units carrying orchestration policy will lose that field on the next deploy. v0.1's clean-deploy authorisation covers this. The GitHub label-roundtrip flow loses its `LabelRoutingPolicy` source of truth and must be reconfigured via the connector binding before the first message after deploy — covered by an explicit migration item in the execution plan.

Rejected: ship a one-deploy compatibility shim that reads the old `orchestration:` block and synthesises runtime instructions. Every existing strategy class would have to translate to instructions for every runtime in the catalogue; the matrix is large, the value short-lived, and the v0.1 cleanup principle says no.

### 10. Out of scope (deliberately)

- **Cost optimisation for unit-side runtime invocations.** Container reuse, warm pools, per-thread session affinity. The mechanisms exist on the agent side; they apply uniformly. No new optimisation lands with this ADR.
- **Per-tenant orchestration tool extension.** A tenant cannot register additional orchestration tools beyond the v0.1 closed set. A future ADR can introduce tenant tooling; this ADR's contract closes the v0.1 surface deliberately to bound the audit space.
- **Heterogeneous tooling per parent.** An agent that is a member of two units does not see different tool sets per parent. The agent has *its own* tool set (skills) and *its own* orchestration tools (its children, if any). The parents' tools are not relevant to the child's runtime.
- **Address-scheme collapse.** `unit://<id>` and `agent://<id>` stay; v0.2 may revisit.
- **`IUnitActor` / `IAgentActor` interface collapse.** The two stay separate; v0.2 may revisit if a uniformity argument materialises.
- **Replacement of every existing `IOrchestrationStrategy` consumer's behaviour as a runtime image.** The ADR removes the strategy types; it does not ship runtime images that reproduce label-routing or workflow-execution semantics. Operators relying on those behaviours migrate via runtime instructions or via the GitHub connector binding (decision 4); image-side reproduction is a v0.2 concern.
- **Workflow-state durability.** The SDK surface (decision 3) makes orchestration tools available to workflow images; it does not provide workflow state or durability guarantees. Image authors choose their own state store.
- **Non-.NET SDK languages.** v0.1 ships `Cvoya.Spring.AgentSdk` for .NET only. Python / TypeScript / Go bindings can be added when there is a real second consumer; for v0.1, runtime images that need the SDK are written in .NET or wrap the HTTP API directly.
- **A general non-orchestration runtime SDK surface.** v0.1's SDK package contains `IOrchestrationClient` and the env-var-bootstrap entrypoint. Other runtime-author surfaces (read inbound message, write outbound, structured logging, configuration access) belong in the same package but are added as concrete consumers materialise; this ADR does not pre-stage them.

## Consequences

**Easier:**

- One concept (agent) on the dispatch dimension; one runtime path; one launcher contract; one cost model.
- The wizard parity #1763 has been reaching for is real, not aspirational — units and agents differ only by the children list, so a single create shape works for both.
- ADR-0038's runtime/model split delivers value end-to-end: the unit's `(runtime, model)` actually runs the unit, instead of being validation-only configuration that no dispatch path consults.
- `OrchestrationDecision` events give operators a uniform record of every delegation, replacing the strategy-specific `decision = "LabelRouted"` payload with a typed shape that every connector and every audit consumer can reason about.
- The platform's "what does this unit do when a message arrives?" question becomes "look at the runtime image and the agent's instructions" — same reasoning operators already apply to leaf agents.
- The SDK surface (decision 3) lets workflow-driven and deterministic agents reach the same orchestration primitives as LLM-driven agents through code, without forcing every image to wrap an MCP client. Image authors keep their workflow technology of choice; the platform does not become a workflow engine.

**Harder:**

- Every responding unit pays runtime-launcher cost per turn. Today's cheapest path (`AiOrchestrationStrategy` round-tripping through `IAiProvider.CompleteAsync` for routing-only units) disappears. Operators with cost-sensitive routing-only units re-tune by either (a) using a small/fast runtime image or (b) accepting the cost. The cost is honest: today's cheap path was a hidden subsidy that broke the moment the unit had no members.
- The orchestration tool contract is a new launcher responsibility. Every runtime image in the catalogue learns to surface tool calls for the v0.1 tool set. `spring-voyage` and the three CLI runtimes (`claude-code`, `codex`, `gemini`) already speak tool calls; the launcher implementations stamp the descriptors and the runtimes call them as they would any other tool.
- A new SDK package and an HTTP callback API: per-invocation token issuance, validation middleware, typed client, sample image, and documentation. Real work, but contained — the package has a small surface (one client interface) and the HTTP endpoints share the platform-side handlers with the LLM tool-call surface (one set of handlers, two transports).
- The GitHub connector's label-routing roundtrip rewires from a `LabelRoutingPolicy`-keyed subscriber to an `OrchestrationDecision`-keyed subscriber with per-binding label rules. This is real work and lands as part of the execution plan.
- Operators with persisted `orchestration:` blocks lose the field; v0.1 clean-deploy covers it but the operator-comms work is real.

**Not abstracted:**

- A pluggable mechanism for runtimes to advertise tool sets back to the platform. v0.1 closes the orchestration-tool surface to a fixed enum; the platform announces the tools, the runtime implements them. A future ADR can invert this if a runtime needs a tool the platform does not enumerate.
- A general "decision evidence" pipeline for non-orchestration agent decisions (e.g. an agent that decides not to respond, or decides to route to a peer outside the unit). v0.1's evidence is scoped to orchestration. Out of scope here.

## Surface affected (delivery scope)

This ADR is implemented as a sequenced multi-PR initiative, tracked under the [#1786](https://github.com/cvoya-com/spring-voyage/issues/1786) umbrella with sub-issues per slice. Detail and ordering live in the execution plan at [`docs/plan/v0.1/units-are-agents.md`](../plan/v0.1/units-are-agents.md). High-level surface:

- **Core domain.** Delete `Cvoya.Spring.Core/Orchestration/*` and `Cvoya.Spring.Core/Policies/LabelRoutingPolicy.cs`. Add `IOrchestrationToolProvider` and `OrchestrationDecision`. Add `IExecutionConfigInheritanceResolver` for multi-parent inheritance. Lands first; everything else depends on it.
- **Dapr layer.** Delete `Cvoya.Spring.Dapr/Orchestration/*`. Rewrite `UnitActor.ReceiveAsync` to invoke the runtime-launcher path. Add the `IOrchestrationToolProvider` implementation that reads the directory.
- **Launcher contract.** Extend the launcher protocol from ADR-0025 to attach orchestration-tool descriptors **and** to mint and inject `SPRING_CALLBACK_URL` + `SPRING_CALLBACK_TOKEN` env vars per invocation. The four built-in launchers (`spring-voyage`, `claude-code`, `codex`, `gemini`) implement both.
- **Dispatcher (orchestration callback API).** New endpoints in `Cvoya.Spring.Dispatcher` for the SDK surface — one per orchestration tool — protected by per-invocation callback-token validation. Both surfaces (LLM tool-call + SDK) dispatch to the same platform-side handlers.
- **Spring Voyage Agent SDK (new project).** `src/Cvoya.Spring.AgentSdk/` — typed `IOrchestrationClient` over HTTP, env-var-bootstrap entrypoint, sample workflow image. Consumed by runtime image authors building workflow-driven runtimes.
- **Web API / OpenAPI / Kiota.** Delete `OrchestrationEndpoints` and `OrchestrationModels`. Add 422 conflict-response shape for multi-parent inheritance. Regenerate Kiota and `openapi-typescript`.
- **Manifest + parser.** Remove the `orchestration:` block; remove `execution.containerRuntime`. Add legacy-shape error mapping per the migration table.
- **CLI.** Remove the positional `<id>` from `spring agent create`. Remove `--container-runtime` from agent-and-unit create commands. Update `spring unit create` to drop any orchestration-related flags.
- **Web portal.** Remove the orchestration-strategy picker on the unit-create wizard. Remove the container-runtime selector. Add the multi-parent inheritance conflict UX (inline error per DESIGN.md §6.3). Update the agent-create wizard per the [`agent-create-redesign.md`](../design/v0.1/agent-create-redesign.md) design.
- **GitHub connector.** Rewrite `Cvoya.Spring.Connector.GitHub/Labels/LabelRoutingRoundtripSubscriber.cs` against the `OrchestrationDecision` event shape. Move per-binding label-roundtrip rules onto the connector binding configuration.
- **Docs.** `docs/concepts/agents.md` ("a unit is an agent" promoted from a comment in code to the canonical concept doc). `docs/concepts/units.md` shrinks (most content moves to `agents.md`, with the unit-only delta — children, permissions, lifecycle — staying). `docs/architecture/orchestration.md` retired or rewritten as a one-pager pointing at the runtime-side reasoning. `docs/architecture/agent-runtime.md` updated for the orchestration-tool surface. `docs/architecture/agent-sdk.md` (new) covers the SDK contract, env-var convention, callback-token shape, and authoring guide for workflow images. `docs/glossary.md` retires "orchestration strategy" / "orchestration policy"; adds "orchestration tools," "orchestration decision," "agent SDK."
- **Tests.** Every layer.
