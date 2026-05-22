# 0051 — One platform MCP server under one auth model: the per-turn callback token folds into the MCP session token

> **Archived — superseded.** Kept for reasoning history; it does not describe the current system. The current decision is [ADR-0054 — One platform MCP server, one execution host](../0054-one-mcp-server-one-execution-host.md). See the [archive index](README.md).

- **Status:** Accepted — the platform exposes its `sv.*` tools through a **single** MCP server with a **single** auth model. The two surfaces in `Cvoya.Spring.Host.Api` today — the worker `spring-voyage` server (long-lived opaque MCP session token) and the messaging surface (short-lived per-turn callback JWT) — collapse into one server keyed off the MCP session token. The session token absorbs the callback token's per-turn delivery-authority claims (`tenantId` / `agentAddress` / `threadId` / `messageId`); the separate callback JWT, its issuer, its validator, and the messaging-specific JSON-RPC handler are deleted. `sv.messaging.send` / `sv.messaging.multicast` join the other `sv.*` tools behind the same `IssueSession` / bearer-token / `ToolCallContext` path. The per-turn delivery authority that the callback token carried is preserved — it becomes a property of the session, which is **already** minted per turn and revoked when the turn ends.
- **Date:** 2026-05-21
- **Related:** [#2589](https://github.com/cvoya-com/spring-voyage/issues/2589) (this decision; the issue stays open for the consolidation work the ADR authorises); [#2578](https://github.com/cvoya-com/spring-voyage/issues/2578) (the `sv.<area>.<verb>` taxonomy + messaging-only delivery surface that left the two-server split as the deferred follow-up); [#2587](https://github.com/cvoya-com/spring-voyage/issues/2587) (relocated the orchestration MCP surface onto `Cvoya.Spring.Host.Api`, removing the Dapr-sidecar reason for the split); [#2583](https://github.com/cvoya-com/spring-voyage/issues/2583) (per-turn callback-token refresh); [#2592](https://github.com/cvoya-com/spring-voyage/issues/2592) / [#2593](https://github.com/cvoya-com/spring-voyage/issues/2593) (the interim 60-minute callback-token lifetime and the in-turn-renewal fix it waits on — both moot once the callback JWT is gone).
- **Related ADRs:** [0050 — Platform MCP tools follow a `sv.<area>.<verb>` taxonomy](0050-platform-mcp-tool-surface.md) §5 explicitly deferred this consolidation to #2589; this ADR is that decision and amends §5. [0049 — Message-delivery tools return a delivery acknowledgement](0049-message-delivery-tool-contract.md): the `sv.messaging.*` delivery-acknowledgement contract is unchanged — only the transport and auth move. [0039 — Units are agents](0039-units-are-agents.md) §3 D12: the per-invocation callback token was introduced there; this ADR retires it as a distinct credential.
- **Related code:** `src/Cvoya.Spring.Dapr/Mcp/{McpServer,McpServerOptions,McpJsonRpc}.cs` (the surviving MCP server), `src/Cvoya.Spring.Core/Execution/IMcpServer.cs` (`IMcpServer` / `McpSession`), `src/Cvoya.Spring.Core/Skills/ToolCallContext.cs` (the per-call caller context the session feeds), `src/Cvoya.Spring.Host.Api/Endpoints/OrchestrationCallbackEndpoints.cs` (the messaging JSON-RPC + REST surface that is deleted), `src/Cvoya.Spring.Core/Runtime/{CallbackToken,CallbackTokenOptions,ICallbackTokenIssuer}.cs`, `src/Cvoya.Spring.Dapr/Auth/{CallbackTokenIssuer,CallbackTokenValidator,CallbackTokenValidationException}.cs` (the callback-JWT machinery that is deleted), `src/Cvoya.Spring.Dapr/Execution/{A2AExecutionDispatcher,DispatcherCallbackEnvironmentBuilder}.cs` (`IssueSession` + callback-token minting on the dispatch path), `src/Cvoya.Spring.Dapr/Orchestration/{MessagingToolHandlers,MessagingToolProvider}.cs` (the delivery handlers re-fronted as an `ISkillRegistry`).

## Context

The platform exposes its MCP tools to agent-runtime containers through **two MCP servers with two different auth models** — both, since [#2587](https://github.com/cvoya-com/spring-voyage/issues/2587), running inside `Cvoya.Spring.Host.Api`:

- **The worker `spring-voyage` server** (`McpServer`, `Cvoya.Spring.Dapr/Mcp/`) serves `sv.directory.*`, `sv.memory.*`, `sv.runtime.*`, and `sv.expertise.*`. Its credential is a **long-lived opaque MCP session token** — a 32-byte random hex string, not a JWT. The dispatcher calls `IMcpServer.IssueSession(agentId, threadId, callerKind)` before launching a container; the server holds the `McpSession` in an in-memory dictionary, validates the `Authorization: Bearer` header against it on every request, and revokes it (`RevokeSession`) when the turn ends. Caller identity reaches a tool through `ToolCallContext(CallerId, CallerKind, ThreadId)`, materialised from the session.
- **The messaging surface** (`OrchestrationCallbackEndpoints`, the `spring-messaging` server) serves `sv.messaging.send` / `sv.messaging.multicast`. Its credential is a **short-lived per-turn callback JWT** minted by `CallbackTokenIssuer`, signed with a per-tenant key, validated by `CallbackTokenValidator`. It carries four claims — `tenantId`, `agentAddress`, `threadId`, `messageId` — and an `exp`.

The split is **historical, not designed**. The callback token predates #2587: orchestration once ran behind its own Dapr sidecar and needed a credential the *container* could carry to an *out-of-process* endpoint. #2587 removed the sidecar — both surfaces are now in the same `Host.Api` process. ADR-0050 §5 then left "two MCP servers remain — for now" as an explicit, scoped follow-up: #2589.

So the question this ADR answers: with both surfaces co-located, **can the per-turn callback token collapse into the MCP session token**, letting one MCP server serve every `sv.*` tool — messaging included — under one auth model?

### What each token actually proves

The callback token looks richer than the session token, but the gap is narrower than it appears.

| Claim / property | Callback JWT | MCP session (`McpSession`) |
|---|---|---|
| Caller address (`agentAddress` / Subject) | `sv_addr` claim | `Subject` — materialised from `(agentId, callerKind)` |
| Thread | `sv_thread` claim | `ThreadId` — passed to `IssueSession`, surfaced via `ToolCallContext` |
| Tenant | `sv_tid` claim | implicit — the worker host runs in one tenant's `ITenantContext`; the dispatcher mints the session inside the dispatching tenant's scope |
| Inbound message id | `sv_msg` claim | **not carried today** |
| Lifetime / expiry | `exp` claim (interim 60 min, #2592) | session is in-memory; lives exactly as long as the turn; `RevokeSession` ends it |
| Revocation | none — a JWT is valid until `exp` | explicit — `RevokeSession` deletes the dictionary entry immediately |
| Integrity | HMAC-SHA256 signature, per-tenant key | unforgeable by construction — a 256-bit random secret, never derived |

Two facts decide it.

**First — the session token is *also* per-turn.** `A2AExecutionDispatcher` calls `IssueSession` at the start of every dispatch and `RevokeSession` at its end. "Per-turn delivery authority — act *as this agent*, on *this thread*" is not something only the callback token can express; the session token is bound to exactly one `(agent, thread)` for exactly one turn, and is *revoked* — not merely *expired* — when the turn finishes. The callback token's `exp` is a strictly weaker form of the same lifetime guard: the session has hard, immediate revocation; the JWT has a soft window (currently a 60-minute interim, #2592, because turns outran the original five minutes — a problem #2593 plans to fix with in-turn renewal). An in-memory session that is deleted on turn-end has neither the leaked-window problem nor the renewal problem.

**Second — the only claim the session lacks is `messageId`.** `tenantId` is ambient. `agentAddress` and `threadId` are already on `McpSession` (as `Subject` and `ThreadId`). `messageId` — the inbound message the turn is responding to — is the *one* piece of per-turn context the session does not carry. It is used in two places: stamped onto the outgoing `Message` built by the messaging handlers (`BuildMcpMessage`), and recorded on any `RoutingDecision` for audit. Both are satisfied by adding `messageId` to `McpSession` and `IssueSession` — the dispatcher already has it at `IssueSession` time (`message.Id`).

Once `messageId` is on the session, the callback token carries **nothing** the session token cannot. The separate JWT, its per-tenant signing key, its issuer, its validator, the interim-lifetime tuning (#2592) and the renewal work (#2593) are all machinery for a credential that no longer needs to exist.

### Why the split costs more than it looks

- **Two auth code paths.** `CallbackTokenValidator` (signature, expiry, issuer/audience, five-claim shape) and the `McpServer` bearer-lookup are independent implementations of "authenticate an inbound MCP request." Two paths, two failure-diagnostic surfaces (`OrchestrationCallbackDiagnostics` exists *only* for callback-token rejections — #2582), two sets of tests.
- **Two JSON-RPC handlers.** `McpServer.HandleToolCallAsync` and `OrchestrationCallbackEndpoints.HandleMcpToolCallAsync` each re-implement `initialize` / `tools/list` / `tools/call`, the `{ content: [{type,text}], isError }` envelope, and bearer extraction. ADR-0050 already had to keep the `sv.<area>.<verb>` taxonomy *consistent across both by hand*.
- **Two tool-exposure models.** The worker server discovers tools from `ISkillRegistry` and gates them through `IToolGrantResolver` (#2379 effective-grant gate) and `IUnitPolicyEnforcer` (#162). The messaging surface hand-rolls a two-tool switch with **none** of that — `sv.messaging.send` / `multicast` are not subject to the effective-grant gate or unit policy. That is a real inconsistency: a unit policy cannot today deny `sv.messaging.send`.
- **The lifetime mismatch is live debt.** #2592 / #2593 exist purely because the callback JWT's static window is the wrong instrument for "lives as long as a turn." The session token already *is* the right instrument.

## Decision

### 1. One platform MCP server, one auth model

The platform exposes every `sv.*` tool — `directory`, `memory`, `messaging`, `runtime`, `expertise` — through a **single** MCP server: the surviving `McpServer` in `Cvoya.Spring.Dapr/Mcp/`. It authenticates every request with the **MCP session bearer token** and no other scheme. The `spring-messaging` JSON-RPC handler in `OrchestrationCallbackEndpoints` is deleted; ADR-0050 §5 ("two MCP servers remain — for now") is superseded by this ADR.

### 2. The MCP session token absorbs the callback token's per-turn authority

`McpSession` and `IMcpServer.IssueSession` gain a `messageId` (`Guid`) — the inbound message the turn responds to. With that, the session carries the full `(tenant, agentAddress, threadId, messageId)` tuple the callback JWT carried:

- `tenant` — ambient via `ITenantContext` (the host runs in one tenant; the session is minted in the dispatching tenant's scope).
- `agentAddress` — `McpSession.Subject`.
- `threadId` — `McpSession.ThreadId`.
- `messageId` — the new field.

`ToolCallContext` gains a matching `MessageId` so a messaging tool reads it the same way `sv.runtime.report_decision` reads `ThreadId` today. The per-turn delivery authority is **preserved in full** — it is now a property of the session, which is minted per turn and revoked on turn-end.

### 3. The callback JWT and its machinery are deleted outright

Per the v0.1 "aggressive cleanup, no back-compat" rule, this PR's follow-up implementation deletes — no shims, no aliases:

- `CallbackToken`, `CallbackTokenOptions`, `CallbackTokenClaimNames`, `ICallbackTokenIssuer`, `ITenantSigningKeyProvider` (if unused elsewhere), `CallbackTokenIssuer`, `CallbackTokenValidator`, `CallbackTokenValidationException`, `OrchestrationCallbackDiagnostics`;
- the `Dispatcher:CallbackToken` configuration section and the `IssuePerMessageCallbackToken` / `DispatcherCallbackEnvironmentBuilder` minting path;
- the `OrchestrationCallbackEndpoints` MCP `tools/call` handler and the `/messaging/send` `/messaging/broadcast` REST routes (see §5 for the REST surface).

#2592 (interim 60-minute lifetime) and #2593 (in-turn renewal) are **closed as obsolete** by the implementation PR — the credential they tune no longer exists.

### 4. `sv.messaging.*` becomes an `ISkillRegistry`, gated like every other `sv.*` tool

`MessagingToolHandlers` / `MessagingToolProvider` are re-fronted as an `ISkillRegistry` (e.g. `SvMessagingSkillRegistry`) registered into `McpServer` alongside `SvDirectorySkillRegistry`, `SvMemorySkillRegistry`, `SvRuntimeSkillRegistry`. The registry's `InvokeAsync` reads `ToolCallContext` — `CallerId`/`CallerKind` give `agentAddress`, `ThreadId` gives the thread, the new `MessageId` gives the inbound message — and calls the existing delivery handlers. Consequences:

- `sv.messaging.send` / `multicast` now pass through the **effective-grant gate** (#2379) and **unit-policy enforcement** (#162) like every other tool. A unit policy *can* deny messaging — closing the inconsistency in Context.
- The ADR-0049 delivery-acknowledgement contract is **unchanged**: each tool is still an RPC returning a delivery ack, never the recipient's reply; delivery is still synchronous with bounded retry; failure is still a synchronous tool error. Only the transport and the credential move.
- The per-thread hop counter (ADR-0050 §4) rides the same handlers and is unaffected.

### 5. The messaging REST sub-routes are removed; the SDK uses the MCP transport

`OrchestrationCallbackEndpoints` also exposes `/messaging/send` and `/messaging/broadcast` as REST routes consumed by `Cvoya.Spring.AgentSdk`'s `MessagingClient` (the typed surface for workflow-driven, non-LLM runtimes). With the callback-token surface gone, the SDK's `MessagingClient` re-points at the unified MCP server, calling `sv.messaging.send` / `multicast` over JSON-RPC `tools/call` with the MCP session bearer token — the same token the runtime already receives for every other `sv.*` tool. One ingress, one credential, for both runtime styles. The launcher env-var contract collapses correspondingly: the container receives a single MCP endpoint + token pair, not an MCP pair plus a callback-endpoint + callback-token pair.

### 6. Tenant integrity is preserved without a signed token

The callback JWT's per-tenant HMAC signature made a forged cross-tenant token "structurally implausible." The unified model is **at least as strong**:

- The MCP session token is a 256-bit cryptographic random secret, never derived from tenant data — unforgeable by construction, not merely signature-checked.
- It is delivered to the container over the launcher's env-var channel and never leaves the `(host, container)` pair; it is in-memory server-side and revoked on turn-end. There is no persisted, transferable artefact to forge.
- Cross-tenant containment stays an **explicit platform-side gate**: `MessageDeliveryService` already re-checks tenant containment via `IOrchestrationTenantResolver` regardless of the credential (see `OrchestrationCallbackEndpoints.TryValidateCallbackAsync`'s comment — the gate is deliberately not derived from the signing-key story). That gate survives the token change untouched, so any future auth shape inherits containment without re-deriving it.

The cloud overlay's extension story is **unchanged in shape**: it swaps `IMcpServer` (or layers session-establishment middleware) via DI exactly as it would have swapped `ITenantSigningKeyProvider` / `CallbackTokenValidator` — one extension seam instead of two.

## Consequences

- One MCP server, one auth path, one JSON-RPC handler, one tool-exposure-and-gating model. ADR-0050's "keep the taxonomy consistent across both servers by hand" maintenance burden disappears.
- `sv.messaging.*` gains effective-grant (#2379) and unit-policy (#162) enforcement it does not have today — a behaviour change: a unit policy can now deny messaging. The implementation PR seeds default grants so existing agents keep their messaging tools.
- The callback-JWT subsystem (issuer, validator, options, per-tenant signing key, diagnostics, the `Dispatcher:CallbackToken` config section) is deleted. #2592 and #2593 close as obsolete.
- `IMcpServer.IssueSession`, `McpSession`, and `ToolCallContext` gain a `messageId` field — appended, per the shared-file convention. Every `IssueSession` call site already has the message in hand.
- The launcher callback-environment contract shrinks: containers receive one MCP endpoint + token, not two endpoint/token pairs. `LauncherCallbackEnvironment` and the AgentSDK environment contract are simplified.
- `Cvoya.Spring.AgentSdk.MessagingClient` switches from REST-over-callback-token to JSON-RPC-over-MCP-session-token. Its public `SendAsync` / `MulticastAsync` shape is unchanged; only the transport beneath it moves.
- Tenant containment for messaging is unchanged in strength — the `IOrchestrationTenantResolver` gate is credential-independent and survives as-is.
- The split's one genuine historical justification — an out-of-process orchestration sidecar — is already gone (#2587). Nothing the consolidation removes is load-bearing post-#2587.

## Alternatives considered

- **Keep two servers; just unify the *token* (issue one credential, accept it on both surfaces).** Rejected: it removes the credential duplication but keeps two MCP servers, two JSON-RPC handlers, two tool-exposure models, and two failure-diagnostic surfaces. The taxonomy still has to be kept consistent by hand, and `sv.messaging.*` still bypasses the effective-grant and unit-policy gates. Half the cost, none of the structural simplification.
- **Keep the callback JWT but move messaging onto the worker server (one server, two accepted credentials).** Rejected: a server that accepts two credential schemes is still two auth code paths with a shared front door — the `CallbackTokenValidator` pipeline, the per-tenant signing key, and #2592/#2593's lifetime debt all survive. The session token already proves everything the JWT proves once `messageId` is added; a second accepted credential is pure retained complexity.
- **Make the MCP session token a signed JWT (so it has a verifiable claim set like the callback token).** Rejected: it adds signing infrastructure to solve a problem the in-memory session model does not have. The session is server-side state with hard revocation; a signed, self-contained token would *reintroduce* the soft-expiry-window problem (#2592) that motivated this consolidation. The opaque random token is the simpler and stronger primitive here.
- **Do nothing — accept the two surfaces as the steady state.** Rejected: ADR-0050 §5 explicitly scoped this as deferred, not settled; #2587 already removed the only architectural reason for the split; and the lifetime mismatch (#2592/#2593) is active debt. The two-server shape is historical residue, and #2588 is blocked on resolving it.
