# 0066 — The `a2a-process` runtime: hosting an external orchestration engine

- **Status:** Accepted
- **Date:** 2026-06-04
- **Related:** [#2591](https://github.com/cvoya-com/spring-voyage/issues/2591) · builds on [ADR-0021](0021-spring-voyage-is-not-an-agent-runtime.md) (not an agent runtime), [ADR-0027](0027-agent-image-conformance-contract.md) (A2A image contract), [ADR-0038](0038-agent-runtime-and-model-provider-split.md) (runtime catalogue), [ADR-0053](0053-units-are-agents-and-one-way-delivery.md) (one-way delivery), [ADR-0055](0055-pull-based-agent-bootstrap.md) (bootstrap), [ADR-0058](0058-spring-voyage-container-contract.md) (container contract)

## Context

Spring Voyage's premise (README; [ADR-0021](0021-spring-voyage-is-not-an-agent-runtime.md), [ADR-0053](0053-units-are-agents-and-one-way-delivery.md)) is that the platform delivers messages and leaves orchestration to an agent's runtime — including the option to *"host their own orchestration frameworks: LangGraph, …"*. Until now nothing exercised that claim: every catalogue team orchestrates by giving an LLM prose instructions (the `magazine` package's managing editor keeps a "running budget" ledger and routes the pipeline turn-by-turn entirely in its system prompt).

We want a second magazine team whose coordinator is a **real workflow engine** (LangGraph) rather than an LLM following instructions — and to build whatever platform support that requires. The coordinator is an **agent member** (the managing editor), not the unit; the magazine director (the unit) still sets editorial direction. Only the managing editor's *runtime* changes.

A workflow engine wants to **call a step and get its result back in-process**. Spring Voyage cross-agent delivery is the opposite: one-way `sv.messaging.send` returns a delivery ack, never a reply, and the reply arrives later as a *new* inbound message ([ADR-0053](0053-units-are-agents-and-one-way-delivery.md)). Two ways to bridge that gap:

- **Per-turn cold process** (the CLI runtimes): the container is spawned per turn, rehydrates from a session file (`claude --resume`), processes one message, exits. An engine would have to checkpoint-and-die every turn.
- **Always-on process**: the container stays up for the unit's life; every inbound message is delivered to the *same live process* as an event. The engine owns its run loop, holds in-flight graph state in memory (with a durable checkpoint for crash recovery), and treats `sv.messaging.send` + a later inbound reply as suspend/resume.

The always-on shape is the natural host for an engine, and the existing native-A2A runtime (`spring-voyage`, [the `spring-voyage-agent` image]) already proves most of it: a persistent Python process that receives each message as an `on_message` event with no forced LLM. What it lacks is (a) a *generic* runtime entry so an engine image needs no bespoke C# launcher, and (b) correct token handling for a long-lived process.

## Decision

### 1. A generic `a2a-process` runtime

Add one runtime catalogue entry, `a2a-process` ([ADR-0038](0038-agent-runtime-and-model-provider-split.md)), backed by a minimal `A2AProcessLauncher` (`LauncherIds.A2AProcess = "a2a-process"`). It is **image-agnostic**: it stamps the platform env contract ([ADR-0058](0058-spring-voyage-container-contract.md) §2.2.1) and the `.spring/system-prompt.md` bundle file, leaves `Argv` empty so the image's own `ENTRYPOINT` runs the engine, and dials the A2A endpoint on `AGENT_PORT`. It launches **without a Dapr sidecar** (the `useDaprSidecar` branch keys on the `spring-voyage-agent` launcher only; any other native-A2A launcher takes the plain container path). An engine author ships a conforming image and selects `ai.runtime: a2a-process` — no platform code change per engine. The SV Agent SDK is the supported way to build the in-container A2A bridge; an engine that already speaks A2A natively needs only the bridge, not the SDK's agentic helpers.

`a2a-process` agents are **persistent** ([hosting: Persistent]): the engine process is the run loop, so it must outlive any single turn. The existing persistent dispatch path ([A2AExecutionDispatcher.DispatchPersistentAsync]) already keeps the container warm and routes each subsequent message to the live endpoint — no dispatcher change is needed for "messages as events."

### 2. Per-message MCP token for always-on processes (SDK)

The dispatcher issues a fresh MCP session token **per turn**, delivers it in the A2A `message/send` metadata under `mcpToken`, and revokes it at turn end. For the CLI bridge this already works (the sidecar rewrites `.mcp.json` each spawn). The native-A2A SDK, however, read `SPRING_MCP_TOKEN` once at `initialize()` and cached it — so on a persistent process every turn after the first calls MCP with a revoked token (401). Worse, at persistent cold-start the dispatcher stamps `SPRING_MCP_TOKEN=""`, and `IAgentContext.load()` *required* it non-empty, so the process could fail to initialise at all.

The fix is in the SDK, not the dispatcher (which already delivers the token correctly):

- `SPRING_MCP_TOKEN` becomes **optional** at `initialize()` — an always-on process gets its token per message, not at boot.
- `Message` carries the **per-message `mcp_token`** (extracted from the inbound A2A metadata). Engine code calls `sv.*` tools with the *current turn's* token.

This is the token model the user anticipated for a long-lived runtime, delivered as a contained SDK change with no new platform token type.

### 3. Structured envelope on the inbound event (SDK)

A deterministic engine wants to switch on structured fields, not parse prose. The platform already renders a structured envelope ([ADR-0060](0060-participant-set-agent-api-and-structured-envelope.md)) into the inbound message as a fenced JSON block. The SDK now parses it and exposes `Message.envelope` (`from`, `to`, `participants`, `message_id`) so the engine reads the sender and conversation as data. (Delivering the envelope as a dedicated A2A data part, rather than embedded in the rendered text, is a clean future refinement; it is not required for a correct consumer today.)

### 4. Durable engine state on the workspace volume

The engine checkpoints to the per-agent workspace volume (`$SPRING_WORKSPACE_PATH`, [ADR-0029](0029-tenant-execution-boundary.md)), which survives container crash, health-restart, redeploy, and resumable stop — reclaimed only on agent delete. LangGraph's checkpointer (SQLite on the volume) is keyed by graph `thread_id`. The engine holds in-flight state in memory while live; the checkpoint is crash insurance and the source of truth across restarts.

### 5. Threads, correlation, and the one mapping the engine must keep

One Spring Voyage conversation maps to one LangGraph graph instance (graph `thread_id` = the SV conversation that started the edition). The engine's run loop is: a new edition message starts a graph run; the graph advances until a node needs a peer (it `interrupt()`s with a delegation spec); the **runtime** — outside the graph — performs the `sv.messaging.send`, ends the turn, and stays alive; when the peer's reply lands as a new inbound event the runtime resumes the graph with `Command(resume=reply)`. The graph stays pure (no I/O); the runtime owns delivery and correlation.

Correlation is the one genuinely hard part. Because a participant-set *is* a thread ([ADR-0030](0030-thread-model.md)), every brief the orchestrator sends to the same writer shares **one** thread — so the reply's `thread_id` cannot say *which* delegation it answers when several are outstanding. v1 resolves this with an explicit **correlation token** the orchestrator embeds in each brief and the package's peer agents are instructed to echo verbatim in their reply; the orchestrator reads it from the reply envelope and resumes the right graph branch. This keeps fan-out/fan-in deterministic with no messaging-core change.

The principled platform fix — an opaque `in_reply_to` / correlation id that `respond_to` round-trips into the inbound envelope, so correlation needs no peer cooperation — is **recommended as the next increment** and specified here, but deliberately not built in this change: it touches shared routing, the envelope, and storage, and the content-token mechanism is sufficient for a correct, observable demonstration. This is the one place we draw the line, and we draw it explicitly rather than silently.

## Consequences

- The premise holds, **non-trivially**: an external engine orchestrates real peer SV agents (not an in-process monolith) on a generic runtime, with the platform growing only a thin, reusable seam.
- Any future engine (CrewAI, ADK, a custom planner) reuses `a2a-process` + the SDK bridge; the catalogue gains an entry, not a launcher.
- The per-message-token SDK fix also repairs a latent multi-turn-token defect on the existing native-A2A path.
- The engine surrenders scheduling to the platform: it runs only when a message is dispatched (or a `reminder`/timer fires). Fan-out is N sends now, N reply-events later; the join lives in the checkpoint, not in prose.
- A deterministic engine makes no LLM calls, but `spring package validate --strict` (ADR-0038) requires every agent to declare a structured `{provider, id}` model. For now the orchestrator declares the team's model to satisfy the contract — the launcher resolves the credential the team already needs and the engine ignores it. Letting model-optional engine runtimes omit it is a candidate refinement.
- **Open / next increments** (specified, not built here): platform-native correlation id in the envelope (removes the echo convention); delivering the structured envelope as an A2A data part; a richer durable-state primitive if the volume's guarantees prove insufficient under concurrent editions; model-optional engine runtimes (see above).
