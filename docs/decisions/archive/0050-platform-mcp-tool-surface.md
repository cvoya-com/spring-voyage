# 0050 — Platform MCP tools follow a `sv.<area>.<verb>` taxonomy; delivery is messaging-only

> **Archived — superseded.** Kept for reasoning history; it does not describe the current system. The current decision is [ADR-0054 — One platform MCP server, one execution host](../0054-one-mcp-server-one-execution-host.md). See the [archive index](README.md).

- **Status:** Accepted — every platform-provided MCP tool is named `sv.<area>.<verb>`: `sv.` marks a platform tool, `<area>` groups tools a model reaches for together, `<verb>` is the action. The areas are `directory`, `memory`, `messaging`, `runtime`, and `expertise`. The platform's message-delivery surface is exactly two tools — `sv.messaging.send` and `sv.messaging.multicast` — on the ADR-0049 delivery-acknowledgement contract. The `delegate_to` / `fanout_to` orchestration tools are **removed**: the platform delivers messages, it does not orchestrate. Recording a routing decision is an *optional* `sv.runtime.report_decision` call, not a side effect of delivery.
- **Date:** 2026-05-21
- **Related:** [#2578](https://github.com/cvoya-com/spring-voyage/issues/2578) (this work); [#2576](https://github.com/cvoya-com/spring-voyage/issues/2576) (message-carried hop counter, implemented here); [#2570](https://github.com/cvoya-com/spring-voyage/issues/2570) / [#2587](https://github.com/cvoya-com/spring-voyage/issues/2587) (the orchestration-tool reshape and its relocation onto `Cvoya.Spring.Host.Api`); [#2589](https://github.com/cvoya-com/spring-voyage/issues/2589) (follow-up — unify the two MCP auth models).
- **Related ADRs:** [0048 — Domain messaging is one-way](0048-event-vs-request-message-semantics.md) and [0049 — Message-delivery tools return a delivery acknowledgement](0049-message-delivery-tool-contract.md): `sv.messaging.send` / `multicast` are the concrete delivery tools that family describes. [0039 — Units are agents](0039-units-are-agents.md) §3–4: the `delegate_to` / `fanout_to` orchestration-tool surface defined there is **superseded** by this ADR — see the amendment note in 0039.
- **Related code:** `src/Cvoya.Spring.Core/Orchestration/MessagingToolName.cs`, `src/Cvoya.Spring.Dapr/Orchestration/{MessageDeliveryService,MessagingToolHandlers,MessagingToolProvider}.cs`, `src/Cvoya.Spring.Dapr/Orchestration/Resources/sv.messaging.*.schema.json`, `src/Cvoya.Spring.Host.Api/Endpoints/OrchestrationCallbackEndpoints.cs` (the `spring-messaging` MCP server), `src/Cvoya.Spring.Dapr/Skills/{SvDirectorySkillRegistry,SvMemorySkillRegistry,SvRuntimeSkillRegistry,DirectorySearchSkillRegistry}.cs` (the worker `spring-voyage` MCP server), the per-thread hop-counter actor.

## Context

The platform exposes MCP tools to agent runtimes through two MCP servers, and the tool names grew without a rule:

- the worker `spring-voyage` server exposed **flat** names — `sv.memory_add`, `sv.get_member`, `sv.report_progress`, `sv.search_expertise`;
- the callback `spring-orchestration` server exposed **un-prefixed** names — `delegate_to`, `fanout_to`.

There was no answer to "where does a new tool's name go," and no shared shape. Adding a peer-communication tool forced the question.

It also surfaced a deeper one. `delegate_to` and `fanout_to` were defined (ADR-0039 §3) when the *platform* orchestrated — routed work down a hierarchy. ADR-0048 / ADR-0049 moved orchestration into the agent runtime: domain messaging is one-way, and a delivery tool returns only a delivery acknowledgement. After that move, `delegate_to` does exactly what a plain message send does — place a message in a mailbox — plus emit one `DecisionMade` activity. `fanout_to` is the same for many targets. The "delegation" is entirely message *content* the recipient's runtime interprets; the platform treats a delegated message and a peer message identically. The orchestration tools no longer carry platform semantics worth a distinct tool.

## Decision

### 1. Every platform MCP tool is named `sv.<area>.<verb>`

`sv.` marks a platform-provided tool. `<area>` groups tools a model reaches for together. `<verb>` is the action (it may be a compound verb — `get_self`, `report_progress`). The areas:

| Area | Tools |
|---|---|
| `sv.directory.*` | `get_self`, `get_member`, `list_members`, `get_siblings`, `get_parents`, `get_status` |
| `sv.memory.*` | `add`, `get`, `list`, `search`, `update`, `delete` |
| `sv.messaging.*` | `send`, `multicast` |
| `sv.runtime.*` | `report_progress`, `report_decision` |
| `sv.expertise.*` | `search`, plus the dynamic per-capability `sv.expertise.{slug}` tools |

`expertise` is its **own** area rather than a `sv.directory.*` verb: the dynamic per-capability tools already publish under the `sv.expertise.` prefix, so `sv.expertise.search` joins an existing family. The runtime-reflection tools group under `sv.runtime.*`.

The taxonomy governs **MCP tool names only**. Connector tools keep their connector-named namespace (`github.*`, …) — `sv.` is reserved for platform tools, and the boundary is explicit. HTTP route paths and SDK method names are a separate surface and are not bound by the taxonomy.

### 2. The platform's delivery surface is `sv.messaging.send` and `sv.messaging.multicast` — nothing else

`delegate_to` and `fanout_to` are **removed outright** (v0.1 — no aliases, no shims). The two messaging tools are the sole way a runtime delivers a message to an addressable target:

- `sv.messaging.send(address, message, reason?)` — one-way delivery to a single target.
- `sv.messaging.multicast(scope | addresses, message, reason?)` — one-way delivery to many, addressed explicitly or by a directory-relationship `scope` (`unit-members`, `siblings`).

Both implement the ADR-0049 contract unchanged: each is an RPC whose response is a **delivery acknowledgement** — the message reached the recipient's mailbox — never the recipient's reply; delivery is synchronous with bounded retry; failure is a synchronous tool error. They differ from each other only in target arity.

### 3. "Delegation" is message content; recording a decision is optional

A runtime that wants to delegate sends a message whose content says so — the platform neither needs nor has a distinct tool for it. A runtime that wants the routing decision recorded on the activity stream calls `sv.runtime.report_decision`, which is **generalized** by this ADR: previously it recorded only decisions that did *not* execute; it now records any routing decision (executed or not). It remains entirely optional — a delivery via `sv.messaging.*` records a plain `MessageSent` activity and nothing more.

### 4. The hop counter rides the single delivery seam

With one delivery seam, delegation-loop prevention (#2576) is implemented once: a per-thread hop counter, incremented on every `sv.messaging.send` / `multicast` call, rejects a call past a platform limit with the validation-class `DepthExceeded` tool error. A cycle `A→B→A→…` on one thread terminates; a normal chain is unaffected.

### 5. Two MCP servers remain — for now

> **Superseded (2026-05-21) by [ADR-0051](0051-unified-platform-mcp-auth-model.md).** ADR-0051 is the [#2589](https://github.com/cvoya-com/spring-voyage/issues/2589) decision this section deferred: the per-turn callback token folds into the MCP session token, and the two MCP servers consolidate into one under a single auth model. The rest of this ADR (the taxonomy, the messaging-only delivery surface) is unchanged.

`sv.messaging.*` stays on the callback `spring-messaging` MCP server (it needs the per-turn callback token's delivery authority); the other `sv.*` tools stay on the worker `spring-voyage` server (long-lived MCP session token). The taxonomy is consistent across both. Consolidating the two servers under one auth model is a separate decision — tracked as [#2589](https://github.com/cvoya-com/spring-voyage/issues/2589).

## Consequences

- `delegate_to` / `fanout_to`, `OrchestrationToolName`, their embedded schemas, the `OrchestrationToolHandlers` delivery handler, the REST `/delegate-to` `/fanout-to` routes, and the SDK delegate/fanout client are deleted. The tool-surface types are renamed to the messaging vocabulary (`MessagingToolName`, `MessagingToolDescriptor`, `IMessagingToolProvider`, `MessagingToolProvider`).
- `RoutingDecision` / `RoutingDecisionKind` / `RoutingDecisionStatus` are retained — `sv.runtime.report_decision` still records decisions. A delivery no longer publishes an `RoutingDecision`; it publishes a `MessageSent` activity.
- The callback MCP server's `serverInfo.name` becomes `spring-messaging`.
- ADR-0039 §3–4 is amended: the orchestration-tool surface it defined is superseded by this ADR.
- ADR-0049's "follow-up #2576" consequence is resolved — the hop counter is implemented here.
- Every future platform MCP tool inherits the `sv.<area>.<verb>` convention; a new tool either fits an existing area or, rarely, defines a new one in an amendment to this ADR.

## Alternatives considered

- **Rename `delegate_to` / `fanout_to` to `sv.orchestration.delegate` / `sv.orchestration.fanout`** (the original #2578 strawman). Rejected: it keeps two tools whose only platform-observable behaviour over `sv.messaging.*` is emitting a `DecisionMade` activity. That is better expressed as an optional, explicit `sv.runtime.report_decision` call than baked into a delivery tool — and it leaves the platform with an "orchestration" surface after orchestration moved into the runtime.
- **Fold `expertise` into `sv.directory.*`.** Rejected: the dynamic per-capability tools already use the `sv.expertise.` prefix; a separate area keeps `sv.expertise.search` consistent with them.
- **Consolidate the two MCP servers now.** Deferred to #2589: the servers use two different auth models (long-lived MCP session token vs per-turn callback token), and collapsing them is its own design question. The taxonomy does not depend on the consolidation.
