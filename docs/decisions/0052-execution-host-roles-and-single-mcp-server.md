# 0052 — One execution host: explicit host roles, a worker-only McpServer, and a single per-turn MCP token

- **Status:** Proposed — this ADR records the design for **Wave 1** of the architecture-consolidation umbrella [#2611](https://github.com/cvoya-com/spring-voyage/issues/2611). Three coupled decisions: (1) the two .NET hosts get **explicit roles** — `spring-api` is a stateless HTTP front door, `spring-worker` is *the* execution host — and the execution *hosted services* register worker-only; (2) the `McpServer` hosted service (port listener + in-process session store) runs in **exactly one** host, the worker, so there is **one** session authority; (3) the agent-container MCP credential collapses to **one per-turn session token**, issued worker-side, delivered fresh each turn in the A2A `message/send`, written into `.mcp.json` by one mechanism. The persistent-agent *deploy* path stops issuing a launch-time MCP session. ADR-0051 / [#2587](https://github.com/cvoya-com/spring-voyage/issues/2587) framed the platform MCP surface as belonging to `Host.Api`; this ADR corrects that — the surface is worker-side, because session lifecycle is per-turn and dispatcher-owned.
- **Date:** 2026-05-21
- **Related:** [#2611](https://github.com/cvoya-com/spring-voyage/issues/2611) (the architecture-consolidation umbrella; this ADR is its Wave 1 design); [#2612](https://github.com/cvoya-com/spring-voyage/issues/2612) (the motivating defect — every persistent-agent turn runs with zero MCP tools); [#2613](https://github.com/cvoya-com/spring-voyage/issues/2613) (host roles); [#2614](https://github.com/cvoya-com/spring-voyage/issues/2614) (single McpServer / session authority); [#2615](https://github.com/cvoya-com/spring-voyage/issues/2615) (single per-turn token contract); [#2609](https://github.com/cvoya-com/spring-voyage/issues/2609) (the `orchestration` terminology sweep — Wave 2 — which removes the dead bridge code this ADR identifies).
- **Related ADRs:** [0051 — One platform MCP server under one auth model](0051-unified-platform-mcp-auth-model.md): 0051 collapsed the *two MCP surfaces* (the `spring-voyage` tool server and the messaging callback surface) into one server under one auth model; it left the surface described as running "inside `Cvoya.Spring.Host.Api`." This ADR is the next layer down — it collapses the *two McpServer instances* (one per host) into one, and corrects the host placement to worker-only. [0012 — Extract container-runtime ownership into `spring-dispatcher`](0012-spring-dispatcher-service-extraction.md): the worker already owns per-turn dispatch through `DispatcherClientContainerRuntime`; this ADR aligns the McpServer with that ownership. [0011 — Persistent-agent lifecycle HTTP surface](0011-persistent-agent-lifecycle-http-surface.md): `PersistentAgentLifecycle` keeps its deploy/scale/logs/undeploy surface; only its MCP-session coupling changes.
- **Related code:** `src/Cvoya.Spring.Dapr/DependencyInjection/ServiceCollectionExtensions.Execution.cs` (the `AddCvoyaSpringExecution` hosted-service registrations gated only by `isDocGen` today), `src/Cvoya.Spring.Dapr/DependencyInjection/ServiceCollectionExtensions.cs` (`AddCvoyaSpringDapr`), `src/Cvoya.Spring.Host.Api/Program.cs` + `src/Cvoya.Spring.Host.Worker/Composition/WorkerComposition.cs` (the two composition roots), `src/Cvoya.Spring.Dapr/Mcp/{McpServer,McpServerOptions}.cs` (the in-process MCP server + its `_sessions` store), `src/Cvoya.Spring.Dapr/Execution/{A2AExecutionDispatcher,PersistentAgentLifecycle,AgentContextBuilder}.cs` (the `IssueSession` / `McpServer.Endpoint` call sites), `src/Cvoya.Spring.Host.Api/Endpoints/{AgentEndpoints,UnitEndpoints}.cs` (the API-side `DeployAsync` callers), `src/Cvoya.Spring.AgentRuntimes/Launchers/*Launcher.cs` (the `.mcp.json` writers), `src/Cvoya.Spring.AgentSidecar/src/{messaging-mcp,a2a}.ts` (the dead per-turn `.mcp.json` refresh path).

## Context

### The motivating defect

Every connector-routed turn on a persistent agent or unit currently runs with **zero MCP tools** (#2612). A unit reads an inbound issue, *decides* a routing target, and then cannot deliver the message or record the decision — `sv.messaging.*` and `sv.runtime.*` are absent from the turn's tool surface. The dogfooding loop (route issue → engineer agent works it) is broken end-to-end.

The root cause is verified: the platform runs **two `McpServer` instances**, one in `spring-api` and one in `spring-worker`, because both hosts call `AddCvoyaSpringDapr`, and `AddCvoyaSpringExecution` registers `McpServer` as an `IHostedService` gated only by design-time tooling (`isDocGen`) — not by host role. Each instance owns its own in-process `_sessions` dictionary, so a bearer token is valid **only on the instance that issued it**.

- Persistent agents are *deployed by the API host*: `AgentEndpoints` / `UnitEndpoints` inject `PersistentAgentLifecycle` and call `DeployAsync`, which calls `IMcpServer.IssueSession`. The launch-time token baked into the container's `.mcp.json` belongs to **`spring-api`'s** McpServer instance.
- The agent's `.mcp.json` points at `host.docker.internal:5050`, which is published by **`spring-worker`** (`deploy.sh` sets `-p 5050:5050` and `Mcp__Port=5050` on the worker container only). The agent's `claude` process dials **`spring-worker`'s** McpServer.
- `spring-worker`'s instance never issued that token → `401` → the CLI drops the `spring-voyage` MCP server for the rest of the session.

### Why this is a structural defect, not a bug

`spring-api` and `spring-worker` both call `AddCvoyaSpringDapr` *identically*. There is no host-role distinction anywhere in the DI graph. As a result, **every** execution hosted service starts in **both** processes: `McpServer`, `PersistentAgentRegistry`, `AgentVolumeManager`, `EphemeralAgentRegistry`, `ContainerHealthMetricsService`. Most of these are tolerable-but-wasteful duplicates (two health-metric timers, two volume-manager sweeps). `McpServer` is *not* tolerable: it holds **per-process in-process state** (the session store), and that state is the authority for an auth decision. Two stores means a token issued against one is rejected by the other.

The dispatch path for connector-routed turns already runs entirely worker-side: webhook → `spring-api` translate → message → `UnitActor` (worker) → routing turn → `AgentActor` (worker) → `A2AExecutionDispatcher` (worker) → `IssueSession` → A2A `message/send`. The dispatcher mints its session against the **worker's** McpServer, and the agent dials the **worker's** McpServer — so for *ephemeral* and *auto-started persistent* agents, the worker-side path is already internally consistent. The breakage is specifically the **API-host-initiated deploy** path issuing a session into the wrong instance. But the fix is not "make deploy issue against the worker" — it is to remove the second instance entirely, so "which instance?" can never be asked again.

### The compounding factor: a dead per-turn token refresh

The TypeScript agent-sidecar bridge carries a per-turn `.mcp.json` token-refresh path (`messaging-mcp.ts`, wired from `a2a.ts`). It is **dead code**: it keys on an MCP server block named `spring-orchestration` and an env var `SPRING_ORCHESTRATION_MCP_CONFIG`, both of which predate ADR-0051. The launchers today write a block named `spring-voyage` and never set `SPRING_ORCHESTRATION_MCP_CONFIG`. So `refreshMessagingToken` always returns `refreshed: false` (no matching block) and the `.mcp.json` token is **never refreshed** — it stays the launch-time token for the life of the container.

For a persistent agent this means: even once the single-McpServer fix lands, a long-lived container would still carry whatever token it was launched with. The session token is per-turn by design (`IssueSession` at dispatch start, `RevokeSession` at dispatch end) — a container that re-uses a launch-time token across turns is holding a token that was revoked after turn 1. The token contract has to become genuinely per-turn, delivered each turn, or the consolidation only half-fixes #2612.

### What the agent-container credential surface looks like today

| Mechanism | Purpose | Disposition |
|---|---|---|
| `SPRING_MCP_URL` / `SPRING_MCP_TOKEN` env vars | MCP endpoint + session token (emitted by `AgentContextBuilder`) | **Keep** — but `SPRING_MCP_TOKEN` becomes per-turn |
| Launch-time token baked into `.mcp.json` by the launcher | What `claude` actually reads | **Becomes** a per-turn write |
| `spring-orchestration` refresh path in the TS bridge | Was meant to refresh `.mcp.json` per turn | **Delete** — keyed on a name/env var nothing emits |
| `SPRING_CALLBACK_URL` / `SPRING_CALLBACK_TOKEN` | OTLP-ingest auth plane (#2492) — a JWT, distinct concern | **Keep, untouched** — separate plane, separate ADR-0051 carve-out |

## Decision

### 1. Two explicit host roles

`spring-api` and `spring-worker` get explicit, named roles:

- **`spring-api` — the stateless HTTP front door.** REST, GitHub webhooks, OpenAPI, the portal BFF, OTLP ingest. It holds **no** execution hosted services.
- **`spring-worker` — the execution host.** Dapr actors, A2A dispatch, the McpServer, agent-container lifecycle, the container registries, EF migrations, the default-tenant bootstrap.

`AddCvoyaSpringExecution` gains an **execution-host gate** — a parameter threaded from `AddCvoyaSpringDapr`, mirroring the existing `isDocGen` gate. The gate controls only the **hosted-service / listener registrations**, not the DI singletons:

- The DI singletons (`McpServer`, `IMcpServer`, `PersistentAgentRegistry`, `AgentVolumeManager`, `EphemeralAgentRegistry`) stay registered in **both** hosts via `TryAddSingleton`, so any code path or OpenAPI doc-gen that resolves them still composes.
- The `AddHostedService(...)` wrappers — the McpServer port listener, the volume-manager sweep, the registry health timers, `ContainerHealthMetricsService` — register **only** when the execution-host role is set, i.e. in the worker.

The worker composition root passes the execution-host role; the API composition root does not. This is one flag, set in two places, with the same shape as the `isDocGen` gate the codebase already understands.

### 2. The McpServer hosted service runs worker-only — one session authority

With the gate from §1, the `McpServer` port listener (`HttpListener` on `:5050`, the accept loop, the `_sessions` store, `IssueSession` / `RevokeSession`) starts in **exactly one** process: the worker. There is one in-process session store; one host issues and validates every session.

This corrects ADR-0051's wording. ADR-0051 collapsed the two MCP *surfaces* and described the result as running "inside `Cvoya.Spring.Host.Api`." That placement is wrong: the MCP session lifecycle is **per-turn and dispatcher-owned**, and the dispatcher (`A2AExecutionDispatcher`) runs worker-side. Co-locating the session authority with the dispatcher that drives it is the correct boundary. `:5050` is already published from the worker container; nothing about the network topology changes.

**A shared / remote session store was considered and rejected.** Backing `_sessions` with Redis or Postgres would let both instances validate any token — but it *preserves the two-instance shape*, the very thing #2611 sets out to remove. A second instance that exists only because a shared store papers over its existence is dead weight. One instance, in-process, is simpler and strictly correct.

### 3. The persistent-agent deploy path stops issuing a launch-time MCP session

`PersistentAgentLifecycle.DeployAsync` runs in the API host (it is called from `AgentEndpoints` / `UnitEndpoints`). Today it calls `IMcpServer.IssueSession` and reads `IMcpServer.Endpoint`. Once §2 lands, the API host's `McpServer` singleton is never started — `Endpoint` is `null` and `IssueSession` would write into a store no agent can reach. Both calls must go.

- **`DeployAsync` no longer issues a session.** A deploy is a launch with no inbound message — there is no turn, no `threadId`, no `messageId` to scope a session to. A session minted at deploy time is meaningless: it would be revoked before the first real turn anyway.
- **The MCP endpoint URL comes from configuration, not the live listener.** `McpServerOptions` already carries `ContainerHost` and `Port`; the container-facing endpoint (`http://host.docker.internal:5050/mcp/`) is derivable from `IOptions<McpServerOptions>` without a started `HttpListener`. `PersistentAgentLifecycle` and any other endpoint-only consumer resolve it from options.
- **The launch-time `.mcp.json` carries the endpoint but no usable token.** A freshly-deployed persistent agent that has not yet received a turn simply has no MCP tools until its first turn — which is correct, because there is no turn context to authorise. Its first dispatched turn delivers a real token (§4).

This keeps `DeployAsync` callable from the API host. The container launch it performs goes through `DispatcherClientContainerRuntime`, a thin HTTP client to `spring-dispatcher` — a stateless forward, not in-process execution state, and therefore not the class of "two of everything" defect this ADR removes. **Moving the explicit-deploy container-launch path fully worker-side is deliberately out of Wave 1 scope** — it is not load-bearing for fixing #2612 (the auto-start path already runs worker-side), and a speculative worker-side deploy RPC is exactly the kind of seam that should be filed, not shipped. A follow-up issue records it.

### 4. One per-turn MCP token, delivered each turn, written by one mechanism

The agent-container MCP credential collapses to **one** token: a per-turn MCP session token.

- **Issued** fresh each turn by the single worker-side McpServer (`A2AExecutionDispatcher.IssueSession`, exactly as it does today for ephemeral and auto-started dispatch).
- **Delivered** to the agent container in the A2A `message/send` for that turn. `SendA2AMessageAsync` adds the token to the request metadata. This closes the gap #2615 names: the bridge already looks for a `callbackToken` metadata field, but the dispatcher does not populate it for persistent containers — so the field gets populated, with the per-turn MCP session token, on every dispatch.
- **Written** into `.mcp.json` by **one** mechanism — the TypeScript bridge, before it spawns the CLI. The bridge rewrites the `spring-voyage` server block's `Authorization` header with the per-turn token from the `message/send` metadata. The CLI re-reads `.mcp.json` on every process start, so the rewrite is picked up on the very next exec.
- **The dead `spring-orchestration` refresh path is deleted.** `messaging-mcp.ts` is rewritten (or replaced) to key on the `spring-voyage` block and a real env var, or folded into `a2a.ts` directly. `SPRING_ORCHESTRATION_MCP_CONFIG` and the `spring-orchestration` block name are removed. This overlaps the #2609 terminology sweep — the two are coordinated so the name is removed once.
- **The OTLP `SPRING_CALLBACK_*` plane is untouched.** It is a distinct auth surface (per ADR-0051's carve-out and #2492); Wave 1 does not touch it.

For an *already-running* persistent agent, `DispatchPersistentAsync` does not currently call `IssueSession` on the warm-container path — it only issues a session when it auto-starts the container. Under this decision, **every** persistent dispatch issues a per-turn session and delivers it in `message/send`, warm container or cold. This is the change that makes the token contract genuinely per-turn rather than launch-time-sticky.

### Resolution of #2612

#2612 is resolved when a fresh persistent-agent turn shows the **worker** McpServer logging a successful `tools/list` with `knownSessions > 0`, and the turn can call `sv.messaging.*` / `sv.runtime.*`. That outcome requires all three decisions: §1 removes the second instance, §2 makes the worker the sole authority, §4 makes the token the agent presents one the worker actually issued for that turn. §3 is the API-host correction that §2 forces.

## Consequences

- **`spring-api` stops doing execution-host work it never should have.** No McpServer listener, no volume sweeps, no health-metric timers, no registry background loops. One fewer `:5050` listener competing for the port; one coherent answer to "where do MCP sessions live."
- **The dogfooding loop is unblocked.** This is the Wave 1 priority — persistent units can route and deliver again.
- **A persistent agent has no MCP tools between deploy and its first turn.** This is a behaviour change, and it is correct: there is no turn context to authorise before the first turn. Operators inspecting a just-deployed, never-dispatched agent will see an empty tool surface; the first routed message populates it.
- **`PersistentAgentLifecycle` and `A2AExecutionDispatcher` decouple from the live `McpServer` instance for the endpoint URL** — they read `IOptions<McpServerOptions>`. The live `Endpoint` property remains for the worker's own use; it is no longer a cross-host dependency.
- **One follow-up filed:** moving the explicit-deploy container-launch path (`PersistentAgentLifecycle.DeployAsync`'s `IContainerRuntime` / `ContainerLifecycleManager` calls) fully worker-side, so the API host delegates rather than forwards. Not load-bearing for #2612; filed, not shipped.
- **Wave 2 (#2608 workspace mount, #2609 terminology, #2610 dead code) and Wave 3** (serving the MCP surface as a route on the worker's Kestrel host instead of a standalone `HttpListener`; re-evaluating whether `spring-dispatcher` still needs to be a separate host process) are unaffected by this ADR and proceed on their own tracks. Wave 3 in particular is a natural successor: once the McpServer is unambiguously worker-only, folding its `HttpListener` into the worker's existing Kestrel pipeline is a localised change.

## Alternatives considered

- **Shared/remote session store** (Redis/Postgres-backed `_sessions`). Rejected — see §2. It validates tokens across instances but preserves the two-instance shape #2611 exists to remove.
- **Make the API-host deploy path issue against the worker's McpServer** (a remote `IssueSession` RPC). Rejected — it keeps a session-issuing code path in the API host and adds a cross-host call, when the simpler truth is that a deploy has no turn context and should not issue a session at all (§3).
- **Keep the launch-time token and add a refresh** (revive `messaging-mcp.ts` against the `spring-voyage` block). Rejected — a launch-time token plus a refresh is two mechanisms where one suffices. The token is per-turn; deliver it per turn (§4). Refresh logic for a credential that should simply be re-delivered is the kind of accumulated machinery #2611 is removing.
