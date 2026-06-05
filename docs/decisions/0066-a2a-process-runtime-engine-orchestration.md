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

### 2. A durable, agent-scoped MCP token for always-on engines

The dispatcher's default MCP token model is **per-turn**: a session is issued for a dispatched message, delivered in the `message/send` metadata under `mcpToken`, and revoked at turn end. That is correct for the CLI runtimes, which only ever run *while processing a message*. An always-on engine is different: it acts on its **own** schedule too — a timer fires, a background task wakes, a workflow step continues after the triggering turn already completed. Those actions have **no inbound message and therefore no per-turn token**, so a per-turn-only model would 401 every `sv.*` call made between messages. (A per-turn token also adds no real security for an always-on container, which persists and can cache anything — the container *is* the trust boundary.)

So the `a2a-process` runtime gets a different token class — a **durable, agent-scoped MCP session**, a *service identity* for the container:

- The dispatcher issues it once at **cold-start** (keyed by `agentId`, no per-turn message binding), delivers it via `SPRING_MCP_TOKEN`, and **does not revoke it per-turn**. It is revoked + re-issued when a fresh container is started for the agent. CLI runtimes keep the per-turn model unchanged — the dispatch path branches on the launcher kind.
- The engine authenticates **every** `sv.*` call with this token — message-, timer-, or background-triggered.
- The same token is echoed in each turn's `mcpToken` metadata so the engine can **refresh** it if a worker restart rotated it; `Message.mcp_token` carries it. `SPRING_MCP_TOKEN` is also made **optional** at `initialize()` so the SDK's required-env check never blocks an engine whose token the platform deferred.

Per-turn delivery authority (`respond_to(message_id)`) still works: the engine passes the `message_id` as a tool argument (from the inbound envelope), it is not derived from the token.

*Known v0.1 limitation:* MCP sessions are in-memory and process-local (the same locality as the per-turn sessions today), so a worker restart invalidates a running engine's durable token until the next cold-start re-issues it. A terminal stop leaves the in-memory session until worker restart — harmless, since the container is gone. A cross-host durable session store is a v0.2 concern.

### 3. Structured envelope on the inbound event, delivered as an A2A `DataPart`

A deterministic engine wants to switch on structured fields, not parse prose. The platform renders a structured envelope ([ADR-0060](0060-participant-set-agent-api-and-structured-envelope.md)) and delivers it **two ways on the same inbound message**:

- as a dedicated A2A **`DataPart`** — `{ "envelopes": [ … ] }`, one self-described envelope per message in the turn's batch (#3056), in the ADR-0064 shape (`from`, `to`, `participants`, `message_id`, `in_reply_to`, `timestamp`, `payload`); and
- as the fenced JSON block embedded in the rendered prose **`TextPart`** (unchanged).

The dispatcher attaches both parts ([A2AExecutionDispatcher.DispatchPersistentAsync]). Every runtime keeps reading the prose `TextPart` — the CLI bridge and LLM runtimes extract text per-part and simply ignore a non-text part — while a deterministic engine reads the `DataPart` as data with no prose round-trip. The SDK exposes `Message.envelope` (`from`, `to`, `participants`, `message_id`, `in_reply_to`) sourced from the `DataPart` when present and **falling back** to parsing the prose block otherwise, so a consumer on an older delivery path still works. The `DataPart` payload and the prose appendix are written by one builder method (`InboundEnvelopeBuilder.WriteEnvelopeObject`) from one resolved participant set, so the two can never drift. Delivering structured data this way is plain A2A — `Part` is a `text | data | file` union — so it needs no platform protocol extension, only that the dispatcher populate the data part and the SDK read it.

### 4. Durable engine state on the workspace volume

The engine checkpoints to the per-agent workspace volume (`$SPRING_WORKSPACE_PATH`, [ADR-0029](0029-tenant-execution-boundary.md)), which survives container crash, health-restart, redeploy, and resumable stop — reclaimed only on agent delete. LangGraph's checkpointer (SQLite on the volume) is keyed by graph `thread_id`. The engine holds in-flight state in memory while live; the checkpoint is crash insurance and the source of truth across restarts.

### 5. Threads, correlation, and the one mapping the engine must keep

One Spring Voyage conversation maps to one LangGraph graph instance (graph `thread_id` = the SV conversation that started the edition). The engine's run loop is: a new edition message starts a graph run; the graph advances until a node needs a peer (it `interrupt()`s with a delegation spec); the **runtime** — outside the graph — performs the `sv.messaging.send`, ends the turn, and stays alive; when the peer's reply lands as a new inbound event the runtime resumes the graph with `Command(resume=reply)`. The graph stays pure (no I/O); the runtime owns delivery and correlation.

Correlation is the one genuinely hard part. Because a participant-set *is* a thread ([ADR-0030](0030-thread-model.md)), every brief the orchestrator sends to the same writer shares **one** thread — so the reply's `thread_id` cannot say *which* delegation it answers when several are outstanding. This is solved with a **platform-native** mechanism, not a per-package convention:

- `sv.messaging.send` already returns the created message's id; the orchestrator records it against the slot+stage the brief is for.
- When the specialist replies via `sv.messaging.respond_to(message_id=…)` — the standard, platform-prompted way to continue a conversation — the platform stamps the reply's `Message.InReplyTo` and surfaces it on the recipient's inbound envelope as `in_reply_to` (a new optional field). The SDK exposes it as `Message.envelope.in_reply_to`.
- The orchestrator matches `in_reply_to` to the recorded brief and resumes the right graph branch.

The specialists echo nothing and need no special instruction — they just reply. Fan-out/fan-in stays deterministic because each brief is a distinct message id. The `in_reply_to` rides the actor's in-memory channel (a `DataMember` on the `Message` record) to the envelope, and is also **persisted** to the `messages` table and surfaced on the message read model (`MessageDetail.in_reply_to`, through the API/OpenAPI/clients), so the Thread Timeline can render reply chains.

## Consequences

- The premise holds, **non-trivially**: an external engine orchestrates real peer SV agents (not an in-process monolith) on a generic runtime, with the platform growing only a thin, reusable seam.
- Any future engine (CrewAI, ADK, a custom planner) reuses `a2a-process` + the SDK bridge; the catalogue gains an entry, not a launcher.
- The per-message-token SDK fix also repairs a latent multi-turn-token defect on the existing native-A2A path.
- The engine surrenders scheduling to the platform: it runs only when a message is dispatched (or a `reminder`/timer fires). Fan-out is N sends now, N reply-events later; the join lives in the checkpoint, not in prose.
- A deterministic engine makes no LLM calls, but `spring package validate --strict` (ADR-0038) requires every agent to declare a structured `{provider, id}` model. For now the orchestrator declares the team's model to satisfy the contract — the launcher resolves the credential the team already needs and the engine ignores it. Letting model-optional engine runtimes omit it is a candidate refinement.
- Correlation is now **platform-native** (§5): `send` returns the message id and `respond_to` round-trips it as `in_reply_to` on the inbound envelope and the persisted message read model, so a deterministic engine matches replies with no per-package echo convention.
- The structured envelope rides a real A2A `DataPart` (§3), so a deterministic engine never parses prose to route — the prose `TextPart` remains only for LLM and CLI runtimes.
- **Open / next increments** (specified, not built here): a richer durable-state primitive if the volume's guarantees prove insufficient under concurrent editions; model-optional engine runtimes (see above). (The web portal rendering reply chains in the timeline from the now-available `in_reply_to` is a UI follow-up.)
