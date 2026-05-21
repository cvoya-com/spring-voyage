# Platform MCP Tools — `sv.<area>.<verb>` Surface

> **[Architecture Index](README.md)** | Source of truth: [ADR-0050](../decisions/0050-platform-mcp-tool-surface.md) | Sibling docs: [Agent SDK](agent-sdk.md), [Agent Runtime](agent-runtime.md)
>
> **Last reviewed:** 2026-05-21
>
> The catalogue of platform-provided MCP tools an agent runtime consumes, the naming taxonomy that governs them, the single MCP server that exposes them, and the messaging-delivery contract that replaced the old orchestration tools.

---

## Audience

This doc is for runtime-image authors building or extending an LLM-driven runtime image (`claude-code`, `codex`, `gemini`, `spring-voyage`, or a new image), and for anyone who needs to know what platform tools an agent sees. Workflow-driven runtimes consume the same `sv.messaging.*` delivery tools through the typed `Cvoya.Spring.AgentSdk` `MessagingClient`, which speaks JSON-RPC against the same MCP server — see [`agent-sdk.md`](agent-sdk.md).

Operators deploying agents do not need to read this doc. The MCP surface is wired by the platform's launchers; nothing here is operator-configurable.

---

## 1. The `sv.<area>.<verb>` taxonomy

Every platform-provided MCP tool is named `sv.<area>.<verb>` ([ADR-0050](../decisions/0050-platform-mcp-tool-surface.md)):

- **`sv.`** marks a platform-provided tool. It is reserved for the platform — connector tools keep their connector-named namespace (`github.*`, `arxiv.*`, …) and the boundary is explicit.
- **`<area>`** groups tools a model reaches for together.
- **`<verb>`** is the action. It may be a compound verb (`get_self`, `report_progress`).

The areas and their tools:

| Area | Tools |
|---|---|
| `sv.directory.*` | `get_self`, `get_member`, `list_members`, `get_siblings`, `get_parents`, `get_status` |
| `sv.memory.*` | `add`, `get`, `list`, `search`, `update`, `delete` |
| `sv.messaging.*` | `send`, `multicast` |
| `sv.runtime.*` | `report_progress`, `report_decision` |
| `sv.expertise.*` | `search`, plus the dynamic per-capability `sv.expertise.{slug}` tools |

`expertise` is its own area rather than a `sv.directory.*` verb because the dynamic per-capability tools already publish under the `sv.expertise.` prefix; `sv.expertise.search` joins an existing family. The runtime-reflection tools (`report_progress`, `report_decision`) group under `sv.runtime.*`.

The taxonomy governs **MCP tool names only**. HTTP route paths and SDK method names are a separate surface and are not bound by it. A new platform MCP tool either fits an existing area or — rarely — defines a new one in an amendment to ADR-0050.

---

## 2. The messaging delivery surface

The platform's message-delivery surface is exactly two tools, `sv.messaging.send` and `sv.messaging.multicast`. They are the sole way a runtime delivers a message to an addressable target.

| Tool | Purpose | Returns |
|---|---|---|
| `sv.messaging.send(address, message, reason?)` | One-way delivery to a single addressable target. | A delivery acknowledgement — never the recipient's reply. |
| `sv.messaging.multicast(scope \| addresses, message, reason?)` | One-way delivery to many targets, addressed explicitly or by a directory-relationship `scope` (`unit-members`, `siblings`). | A per-target delivery acknowledgement. |

Both implement the [ADR-0049](../decisions/0049-message-delivery-tool-contract.md) delivery-acknowledgement contract: each is an RPC whose response confirms the message reached the recipient's *mailbox*, not that the recipient processed or replied to it. Delivery is synchronous with bounded retry; a failure is a synchronous tool error. The two tools differ only in target arity.

`reason` is optional. It is recorded verbatim — runtimes that surface internal model reasoning **must redact it** before passing it, because the field is operator-visible audit.

A successful `sv.messaging.*` call records a `MessageSent` activity and nothing more. It does **not** publish an `RoutingDecision`.

### "Delegation" is message content, not a platform tool

The platform has **no** `delegate_to` / `fanout_to` orchestration tools — they were removed outright in v0.1 with no aliases or shims ([ADR-0050 § 2](../decisions/0050-platform-mcp-tool-surface.md), superseding [ADR-0039 §§ 3–4](../decisions/0039-units-are-agents.md)). Domain messaging is one-way ([ADR-0048](../decisions/0048-event-vs-request-message-semantics.md)) and the platform delivers messages; it does not orchestrate.

A runtime that wants to *delegate* sends a message via `sv.messaging.send` whose **content** says so — the platform treats a delegated message and a peer message identically; the recipient's runtime interprets the content. "Delegation" is therefore a runtime-level concern, not a platform tool.

A runtime that wants the routing decision recorded on the activity stream calls `sv.runtime.report_decision` (see § 3). That call is optional and independent of delivery.

---

## 3. Recording a routing decision is optional

`sv.runtime.report_decision` records a routing decision on the activity stream as a `DecisionMade` activity carrying an `RoutingDecision` payload. ADR-0050 **generalised** the tool: previously it recorded only decisions that did *not* execute; it now records any routing decision — executed or not.

Calling it is entirely optional. A runtime delivering a message via `sv.messaging.*` records a plain `MessageSent` activity; if it also wants the *decision* visible (the intended targets and the rationale), it makes a separate `sv.runtime.report_decision` call. The two are not coupled — neither implies the other.

This is the right shape for decisions that cannot be carried out: a runtime that decided where to route but found delivery impossible (the messaging tool unavailable, a delivery failure, no valid target) still calls `sv.runtime.report_decision` so the operator sees the decision even though no `MessageSent` activity exists.

`sv.runtime.report_progress` records free-text turn progress; it is the catch-all "here is what this turn did" call, distinct from the structured `report_decision`.

The `RoutingDecision` event shape and the `DecisionMade` activity are documented in [`agent-runtime.md` § 4d](agent-runtime.md#4d-decisionmade-event-shape).

---

## 4. The per-thread hop counter

With a single delivery seam, delegation-loop prevention is implemented once. A **per-thread hop counter** is incremented on every `sv.messaging.send` / `sv.messaging.multicast` call. A call past the platform limit is rejected with the validation-class `OrchestrationDepthExceeded` tool error.

This terminates a cycle `A→B→A→…` carried on a single thread while leaving a normal delegation chain unaffected. The counter rides the message on the thread; it is not a per-runtime or per-container counter.

---

## 5. The single platform MCP server

The platform exposes **every** `sv.*` tool through one MCP server under one auth model (ADR-0051).

| MCP server | `serverInfo.name` | Tools | Auth |
|---|---|---|---|
| Platform MCP server | `spring-voyage` | `sv.directory.*`, `sv.memory.*`, `sv.runtime.*`, `sv.expertise.*`, `sv.messaging.*` | MCP session bearer token. |

The MCP session is minted per turn by the dispatcher (`IMcpServer.IssueSession`) and revoked when the turn ends. It carries the per-turn `(tenant, agentAddress, threadId, messageId)` delivery authority `sv.messaging.*` needs — the same authority the retired per-turn callback JWT used to carry. Because messaging is now a registry on this server, `sv.messaging.send` / `sv.messaging.multicast` pass through the same effective-grant gate (#2379) and unit-policy enforcement (#162) as every other `sv.*` tool: a unit policy can deny messaging, and the grant resolver's platform tier surfaces the messaging tools to every agent / unit subject by default.

### Per-runtime attachment

A launcher attaches the single MCP server using each runtime's native mechanism. For the CLI runtimes (`claude-code`, `codex`) the launcher writes a one-server `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "spring-voyage": {
      "type": "http",
      "url": "<MCP endpoint>",
      "headers": { "Authorization": "Bearer <MCP session token>" }
    }
  }
}
```

`gemini` reads the same shape from `.gemini/settings.json` (using `httpUrl` instead of `url`). `tools/list` is grant-filtered server-side, so the runtime discovers exactly the `sv.*` tools — messaging included — its subject is entitled to.

Workflow-driven runtimes consume the same delivery handlers through the typed `Cvoya.Spring.AgentSdk` `MessagingClient`, which calls `sv.messaging.send` / `sv.messaging.multicast` over JSON-RPC `tools/call` against the same MCP server with the same session token — see [Agent SDK](agent-sdk.md).

---

## See also

- [ADR-0051](../decisions/0051-unified-platform-mcp-auth-model.md) — one platform MCP server, one auth model; `sv.messaging.*` folds onto it.
- [ADR-0050](../decisions/0050-platform-mcp-tool-surface.md) — the `sv.<area>.<verb>` taxonomy; messaging-only delivery; the canonical decision.
- [ADR-0049](../decisions/0049-message-delivery-tool-contract.md) — the delivery-acknowledgement contract `sv.messaging.*` implements.
- [ADR-0048](../decisions/0048-event-vs-request-message-semantics.md) — domain messaging is one-way.
- [ADR-0039](../decisions/0039-units-are-agents.md) — units-are-agents; §§ 3–4 superseded by ADR-0050.
- [Agent SDK](agent-sdk.md) — the typed `MessagingClient` for workflow-driven runtimes; same delivery tools over the MCP transport.
- [Agent Runtime](agent-runtime.md) — the launcher tier that attaches the platform MCP server.
- [Units & Agents](units.md) — unit entity model and how a runtime delivers to members.
