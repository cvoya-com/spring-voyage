# 0054 — One platform MCP server, one execution host

- **Status:** Accepted — 2026-05-22. **Re-baseline.** This record restates the current design directly. It consolidates and supersedes the archived [0050](archive/0050-platform-mcp-tool-surface.md) (the `sv.<area>.<verb>` taxonomy + messaging-only delivery), [0051](archive/0051-unified-platform-mcp-auth-model.md) (one MCP server, one auth model), and [0052](archive/0052-execution-host-roles-and-single-mcp-server.md) (explicit host roles, worker-only `McpServer`, single per-turn token). Those three were Waves of the [#2611](https://github.com/cvoya-com/spring-voyage/issues/2611) architecture-consolidation umbrella; each corrected its predecessor's placement (0051 put the server "inside `Host.Api`"; 0052 moved it to the worker), so the live design only reads cleanly when all three are folded together. This record is that fold. The archived ADRs keep the rejected alternatives — shared session stores, two-credential servers, signed session tokens.
- **Date:** 2026-05-22
- **Related ADRs:** [0053](0053-units-are-agents-and-one-way-delivery.md) — the one-way delivery and acknowledgement contract the `sv.messaging.*` tools implement. [0012](0012-spring-dispatcher-service-extraction.md) — `spring-dispatcher` owns container-runtime launch; the worker owns per-turn dispatch. [0015](0015-dapr-as-infrastructure-runtime.md) — Dapr actors run in the execution host. [0029](0029-tenant-execution-boundary.md) — the A2A wire the per-turn token rides on.
- **Related docs:** [`docs/architecture/components.md`](../architecture/components.md), [`docs/architecture/messaging.md`](../architecture/messaging.md), [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md).

## Context

The platform exposes MCP tools to agent-runtime containers. That surface accumulated "two of everything": two `McpServer` instances (one per .NET host, because both hosts called `AddCvoyaSpringDapr` identically), two token families (a long-lived MCP session token and a short-lived per-turn callback JWT), two JSON-RPC handlers, two tool-exposure-and-gating models. The session store is in-process per instance, so a token issued by one host's `McpServer` was rejected by the other's — which meant **every connector-routed turn on a persistent agent ran with zero MCP tools** (the launch-time token belonged to `spring-api`'s instance; the agent dialled `spring-worker`'s).

The root cause was a missing host-role distinction. The dispatch path for a turn — webhook → translate → `UnitActor` → routing turn → `AgentActor` → `A2AExecutionDispatcher` → A2A `message/send` — runs entirely worker-side. The MCP session is per-turn and dispatcher-owned. Co-locating the session authority with the dispatcher that drives it is the correct boundary; the rest is duplication to remove.

## Decision

### 1. Two explicit host roles

| Host | Role | Owns |
|------|------|------|
| **`spring-api`** (`Host.Api`) | Stateless HTTP front door | REST API, GitHub webhooks, OpenAPI, OTLP ingest, the portal's backing API. No execution-hosted services. |
| **`spring-worker`** (`Host.Worker`) | The execution host | Dapr actors (Agent/Unit/Human/ThreadHop), A2A dispatch, the `McpServer`, agent-container lifecycle, the container registries, EF migrations, the default-tenant bootstrap. |

`AddCvoyaSpringDapr` takes a `SpringHostRole` (`HttpFrontDoor` | `ExecutionHost`). The role gates the execution **hosted services** — the `McpServer` listener, the container registries, the volume and health timers — so they start only in the worker. The API host's persistent-agent endpoints (`deploy` / `undeploy` / `scale` / `deployment-status` / `logs`) delegate to the worker over Dapr service invocation; the API host runs no execution work itself.

### 2. One platform MCP server, served by the worker

There is exactly one `McpServer` instance, in the worker, with one in-process session store. It is served as a route (`POST /mcp/`) on a dedicated Kestrel endpoint (the MCP port, default `5050`), kept off the Dapr app-channel port so the MCP surface and the actor surface stay separated. A shared/remote session store was rejected: it would let two instances validate any token, but it preserves the two-instance shape this consolidation removes.

### 3. Every platform MCP tool is named `sv.<area>.<verb>`

`sv.` marks a platform tool; `<area>` groups tools a model reaches for together; `<verb>` is the action. The areas:

| Area | Tools |
|------|-------|
| `sv.directory.*` | `get_self`, `get_member`, `list_members`, `get_siblings`, `get_parents`, `get_status` |
| `sv.memory.*` | `add`, `get`, `list`, `search`, `update`, `delete` |
| `sv.messaging.*` | `send`, `multicast` |
| `sv.runtime.*` | `report_progress`, `report_decision` |
| `sv.expertise.*` | `search`, plus the dynamic per-capability `sv.expertise.{slug}` tools |

`sv.` is reserved for platform tools. Connector tools keep a connector-named namespace (`github.*`, …). A new platform tool either fits an existing area or defines a new one in an amendment to this record.

### 4. The delivery surface is exactly two tools

The platform delivers messages; it does not orchestrate (see [ADR-0053](0053-units-are-agents-and-one-way-delivery.md)). Its delivery surface is exactly:

- `sv.messaging.send(address, message, reason?)` — one-way delivery to one target.
- `sv.messaging.multicast(scope | addresses, message, reason?)` — one-way delivery to many, addressed explicitly or by a directory-relationship `scope` (`unit-members`, `siblings`).

Both implement the ADR-0053 delivery-acknowledgement contract. There is no `delegate_to` / `fanout_to`: a runtime that wants to delegate sends a message whose content says so. Recording a routing decision on the activity stream is an optional, explicit `sv.runtime.report_decision` call. Delegation-loop prevention rides this single delivery seam as a **per-thread hop counter** (`ThreadHopActor`, one actor per thread): every `send` / `multicast` increments it and a call past the platform limit is rejected as a validation-class tool error.

### 5. One per-turn MCP session token

The agent-container MCP credential is a single token: a per-turn MCP session token.

- **Issued** fresh each turn by the worker `McpServer` (`IssueSession` at dispatch start; `RevokeSession` at turn-end). It carries the full `(tenant, agentAddress, threadId, messageId)` tuple.
- **Delivered** to the container in the A2A `message/send` metadata for that turn.
- **Written** into the container's MCP config (`.mcp.json`, or the runtime's equivalent) by one mechanism — the TypeScript sidecar bridge — before it spawns the runtime CLI.
- **Authenticates** every `sv.*` tool call as an opaque bearer token. It is a 256-bit random secret, unforgeable by construction, in-memory server-side, and hard-revoked on turn-end — there is no separate callback JWT, no per-tenant signing key, no soft-expiry window.

A freshly-deployed persistent agent that has not yet received a turn has no MCP tools — correct, because there is no turn context to authorise. Its first dispatched turn delivers a real token.

### 6. One gating model

`sv.messaging.*` is exposed as an `ISkillRegistry` like every other `sv.*` tool, so it passes the same gates: the effective-grant gate (#2379) and unit-policy enforcement (#162). A unit policy can deny messaging exactly as it can deny any other tool. Tenant containment is an explicit, credential-independent platform-side gate inside the delivery handler.

## Consequences

- One `McpServer`, one session store, one auth path, one JSON-RPC handler, one tool-exposure-and-gating model. "Which instance issued this token?" can no longer be asked.
- Persistent-agent turns run with their full tool surface — the motivating defect (#2612) is resolved.
- `spring-api` is a genuine stateless front door: it registers no execution singletons and delegates persistent-agent lifecycle to the worker.
- The launcher's container-credential contract is one MCP endpoint + one per-turn token — not an MCP pair plus a callback pair.
- The OTLP ingest auth plane (`SPRING_CALLBACK_*`, a distinct per-invocation JWT for telemetry ingest) is a separate surface and is untouched by this record.

## Revisit criteria

- Horizontal scale-out of the execution host (more than one `spring-worker`) reopens the in-process session store — it would need a sticky-routing or shared-store design. v0.1 runs a single worker.
- A second non-LLM transport for the `sv.*` tools (beyond the AgentSDK's JSON-RPC-over-MCP client) would prompt a transport amendment, not an auth-model change.
