# 0041 — Actor-runtime contract for agent containers (per-thread session resume + concurrent-threads modes)

- **Status:** Accepted — 2026-05-10 — supersedes the implicit "long-lived in-process state per agent" assumption with an explicit two-mode contract bound to `concurrent_threads`. Sharpens [ADR-0026](0026-per-agent-container-scope.md) (per-agent container scope) by specifying *what happens inside* the container when N threads share it.
- **Date:** 2026-05-10
- **Tracks:** [#2090](https://github.com/cvoya-com/spring-voyage/issues/2090). Triggered by [#2088](https://github.com/cvoya-com/spring-voyage/issues/2088) / PR [#2093](https://github.com/cvoya-com/spring-voyage/pull/2093) (uvicorn-after-turn shutdown).
- **Related code:** `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Dapr/Execution/PersistentAgentRegistry.cs`, `src/Cvoya.Spring.Dapr/Actors/AgentActor.cs` (per-thread channels from #2076 / #2078), `agents/spring-voyage-agent-sdk/spring_voyage_agent_sdk/runtime.py`, `src/Cvoya.Spring.AgentSidecar/src/bridge.ts`.
- **Related docs:** [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md), [`docs/architecture/agent-sdk.md`](../architecture/agent-runtime.md), [ADR-0026 — Per-agent container scope](0026-per-agent-container-scope.md), [ADR-0017 — A Unit IS an Agent](0017-unit-is-an-agent-composite.md), [ADR-0030 — Thread model](0030-thread-model.md), [ADR-0036 — Single-identity model](0036-single-identity-model.md), [ADR-0058 — Spring Voyage container contract](0058-spring-voyage-container-contract.md) (the canonical container contract; the thread-binding env vars `SPRING_THREAD_ID` / `SPRING_THREAD_ID_ARG_CREATE` / `SPRING_THREAD_ID_ARG_RESUME` and the per-runtime CLI session-storage env vars `CLAUDE_CONFIG_DIR` / `GEMINI_CLI_HOME` this ADR introduces are catalogued there in §1).

## Context

ADR-0026 fixed *what gets a container* (one per agent). It left open *what happens inside that container when an agent serves many threads*. PR #2076 / #2078 reshaped `AgentActor`'s mailbox to per-thread channels honouring `concurrent_threads`, but the runtime-side contract — how the agent's process maintains state across turns and across threads — was never written down. The literal symptom in #2088 (uvicorn dying after one turn for the Python SDK) revealed three implicit assumptions that disagree across runtime paths:

1. **Where conversational memory lives.** Spring Voyage Agent (Python SDK) implicitly assumed in-memory; CLI runtimes (Claude Code, Codex, Gemini) assumed per-invocation cold start with state on disk via `--resume`.
2. **What the runtime's session identifier is.** The Python SDK has no external session concept; CLI runtimes generate or accept a session id per process. Spring Voyage threads have `Guid` ids (ADR-0036) that go unused as the runtime's session identifier.
3. **What `concurrent_threads` means at the runtime layer.** The mailbox honours it; the runtime contract does not say what an author signs up for when they set it `true`.

Two architectural shapes were considered for v0.1 and rejected (see Alternatives). What landed is the simplest model that fits the existing actor mailbox and the existing CLI session-resume primitives.

## Decision

**One container per agent (ADR-0026 unchanged). Per-thread conversational memory lives on disk in the runtime's session storage, keyed by `thread.id`. The platform-side actor mailbox controls in-flight concurrency inside the container, governed by `concurrent_threads`.**

### `thread.id` IS the session identifier

The runtime's session identifier is `thread.id` verbatim. No hashing, no derivation, no namespacing.

- Spring Voyage `thread.id` is a `Guid` (ADR-0036) — globally unique by construction.
- The agent identity is implicit in the container's filesystem (per-agent workspace volume — ADR-0026); session files for two different agents never share a directory.
- For CLI runtimes the bridge invokes the runtime with `--resume <thread.id>` (Claude Code) or its equivalent (Codex `--continue` with id, Gemini equivalent).
- For the Python SDK the agent author receives `thread.id` on each `on_message` call; thread-local state lives under `$SPRING_WORKSPACE_PATH/threads/<thread.id>/`.

#### `thread_id` is developer/plumbing-only — never model-visible (#3079)

`thread.id` is the **participant-set identity** (ADR-0030): it is stable for a
given set of participants and does **not** vary per message exchange. That makes
it correct as a session-resume / workspace-scoping key (above), but a trap as a
"this run / this exchange" key — the magazine `a2a-process` orchestrator keyed a
per-edition concept off it and got one edition for the agent's entire life
(#3072 → worked around in #3077 with an engine-minted `edition_id`). The root
cause is that an agent runtime / LLM reads the word "thread" and assumes it is
the lifetime identifier of an interaction. So the boundary is:

- **Permitted (developer + plumbing surface):** the typed SDK fields
  (`Message.thread_id`, `IAgentContext.thread_id`, `thread_workspace(...)`), the
  `SPRING_THREAD_ID` container env var, `ThreadIdRegistry`, and the internal
  `thread.id` routing/session key. A developer (or orchestrator *code*) holding
  the concept is fine.
- **Forbidden (model-visible surface):** the system prompt, tool
  descriptions / input schemas, tool-call results, and the rendered inbound
  envelope MUST NOT contain "thread" / `thread_id`. The model keys "this
  exchange" on `message_id` and "who" on the participant set; the inbound
  envelope already omits `thread_id` (#2747), and the platform-tool catalog and
  tool descriptions are scrubbed to match (#3079). A negative-pin test
  (`ModelVisibleThreadJargonTests`) fails the build if "thread" re-enters the
  tool surface; `PlatformPromptProvider` tests pin the prompt.
- **Runtime correlation id, when one is needed, is the A2A `context_id`** — the
  runtime's own vocabulary — not `thread_id`. SV sets `contextId == thread.id`
  (above), which the A2A spec permits: `contextId` is *"the contextual
  collection of interactions"* with only MAY/SHOULD guidance — granularity and
  lifetime are implementation-defined. SV's mapping is at the **coarse extreme**
  of that latitude (one `context_id` for the participant set's whole life, never
  reset), so `context_id` is documented to runtimes/LLMs as a **lifetime-stable
  grouping key, not A2A's restartable-session default** — preventing the same
  per-run mis-key off `context_id` that #3072 hit off `thread_id`.

### Two modes, bound to `concurrent_threads`

| `concurrent_threads` | Mailbox behaviour | In-container concurrency | Head-of-line (HoL) blocking | In-process conflict surface |
| --- | --- | --- | --- | --- |
| **`false`** (default) | Per-agent serialization across all threads. | One runtime invocation in flight at a time. | Yes — long turn on thread A queues thread B. | None. |
| **`true`** (opt-in) | Per-thread channels dispatch independently. | N concurrent runtime invocations (one per active thread). | No. | Author-owned (ports, `/tmp`, `~/.cache`, child PIDs, network state, env globals). |

The `false` default is safe for any agent. The `true` mode is an explicit author opt-in with a published contract; agents that cannot meet the contract stay on `false`.

#### HoL scope: per-agent, not per-thread

HoL blocking applies to the agent's own mailbox only. A thread is a participant set ([ADR-0030](0030-thread-model.md)); the actor mailbox is per-agent ([ADR-0026](0026-per-agent-container-scope.md)). A busy agent does not block its co-participants in the same thread.

Worked example. Two threads share a participant:

- Thread 1 = {agent A, agent B, human 1}
- Thread 2 = {agent A, human 1}

Human 1 asks A to do long-running work on thread 2. A's mailbox is now busy. Human 1 then posts on thread 1. **B responds normally** — B has its own actor and its own container; A's busy state does not touch B. **A is silent on thread 1** until it drains thread 2; thread 1's messages addressed to A queue in A's mailbox and are processed when A is free. Thread 1's conversation between B and human 1 continues at full speed.

The user-visible failure mode of A's silence is a portal concern (rendering A's runtime status to humans in thread 1) — tracked under [#2100](https://github.com/cvoya-com/spring-voyage/issues/2100), not a model change.

### The `concurrent_threads: true` author contract

An agent that sets `concurrent_threads: true`:

- MAY hold per-thread state in-process keyed by `thread.id`.
- MUST NOT bind fixed ports (every per-thread port allocation must be ephemeral / dynamic).
- MUST NOT write outside `$SPRING_WORKSPACE_PATH/threads/<thread.id>/` for thread-local state.
- MUST NOT assume any tool's child processes are uniquely theirs (no `pkill -f pytest` patterns).
- MUST NOT mutate shared global state (env vars, working directory, signal handlers).
- For CLI runtimes specifically: the system prompt MUST forbid the model from invoking long-running watchers (`pytest --watch`, `npm run dev`, etc.) — these are concurrency-mode footguns.

The contract is documented in author-facing docs ([`docs/architecture/agent-sdk.md`](../architecture/agent-runtime.md) and the equivalent CLI-runtime guidance) and surfaced as warning text in `spring agent validate` when an agent sets `concurrent_threads: true`.

## Alternatives considered

- **Long-lived stdin pipe per thread inside the container.** Bridge maintains one persistent CLI child per thread, frames N messages over stdin/stdout. Originally favoured for avoiding cold-start. **Rejected.** Cold-start per message via `--resume` is simpler, gets the same actor-serialized semantics, and avoids one long-lived child process per thread. The stdin-pipe shape stays available as a future optimization if cold-start latency becomes a bottleneck.
- **Container-per-thread.** Each Spring Voyage thread gets its own container, idle-timeout teardown. Full isolation. **Rejected for v0.1.** Container count tracks active-thread count instead of agent count — unbounded and spiky; cold-start latency per thread; `docker pause` is cgroup-freezer only and still holds memory pages, so it doesn't solve density at scale. Reconsider if thread-spiky workloads make HoL on `false` plus replicas on `true` insufficient. Tracked under #2090 sub-issue (e).
- **Native sub-agent + worktree delegation.** Use Claude Code `Task` tool / `git worktree` integration to multiplex Spring Voyage threads inside one CLI process. **Rejected.** Sub-agents are fork-join (parent invokes, sub-agent runs to completion, returns one result, gone) and can't represent multi-turn threads — there's no "send another message to the same sub-agent." `git worktree` solves filesystem isolation only; ports, PID namespace, caches, network namespace, OOM blast radius, credentials via env are still shared. Wrong abstraction for thread multiplexing.
- **tmux + terminal scrape.** Keep CLI alive in tmux, send keys, capture pane. **Rejected.** Terminal scraping is fragile (ANSI / prompt detection), doesn't compose with A2A, operationally heavy at multi-tenant scale.
- **Per-thread sandbox CLI** (`sbx`, named Podman sandboxes). **Rejected.** Functionally equivalent to "container per thread, ephemeral" with a different CLI on top — re-implements `ContainerLifecycleManager` + `PersistentAgentRegistry`.

## Consequences

### Gains

- **Unifies the Python SDK and CLI runtime paths under one contract.** Both become "actor-serialized at the platform, thread-keyed memory at the runtime, single-thread-active-at-a-time semantics." The literal #2088 fix (keep uvicorn alive) and the CLI-runtime session-resume wiring are the same architectural answer expressed at two different layers.
- **No new lifecycle code for `concurrent_threads: true` on CLI runtimes.** `bridge.ts` already spawns per `message/send` with no serialization (`src/Cvoya.Spring.AgentSidecar/src/bridge.ts:61`) — concurrent A2A requests already produce concurrent children. The new bit is the deterministic `thread.id` → session-id wiring, which is a documentation + small implementation change.
- **`thread.id` reuse keeps identifiers traceable end-to-end.** A session file on disk inside an agent container can be matched 1:1 to a Spring Voyage thread without an indirection table. Debugging across the actor mailbox, the dispatcher, the bridge, and the runtime's local files reads as one identifier.
- **Per-agent isolation matches per-agent credential / image / cancellation surface from ADR-0026.** Blast radius rules from ADR-0026 still hold; nothing in this ADR widens them.

### Costs

- **HoL blocking under `concurrent_threads: false` is real and visible.** A long turn on thread A queues thread B inside the same agent. Mitigations: (1) agent-design discipline (long work goes async behind a tool call); (2) opt-in to `true` for stateless agents; (3) v0.2 replicas (see [#2090 sub-issue (d)](https://github.com/cvoya-com/spring-voyage/issues/2090)). Acceptable for v0.1 — failure mode is "perceived slowness", not data loss or crash.
- **Author burden under `concurrent_threads: true`.** The contract is non-trivial and easy to violate accidentally (especially around child processes). Mitigated by `spring agent validate` surfacing it as an explicit opt-in warning, and by making `false` the default so authors only encounter the contract when they reach for it.
- **Cold-start cost per message for CLI runtimes.** Every message spawns a fresh `claude` / `codex` / `gemini` process. The runtime loads its session from disk (cheap; not a model context replay over the wire — the session file is a serialized prior conversation). Order ~1–3s today; if it dominates dispatch latency, the long-lived stdin-pipe alternative remains available without changing this ADR.

### Known follow-ups

- **#2090 sub-issues (a)–(c):** v0.1 implementation work — bridge.ts session-id mapping, Python SDK thread-state convention, `concurrent_threads: true` author-contract docs + `spring agent validate` warning.
- **#2090 sub-issue (d):** v0.2 — per-agent warm pool / replicas. Lifts HoL blocking on `false` by giving an agent N parallel actor mailboxes.
- **#2090 sub-issue (e):** v0.2 — container-per-thread isolation mode, gated on whether the v0.1 model proves insufficient under thread-spiky workloads.

## Revisit criteria

Revisit if any of the below hold:

- An agent class emerges that genuinely needs in-process per-thread memory at scale (large model contexts that don't fit on disk efficiently, or runtimes whose `--resume` cold-start cost dominates dispatch latency by >5x). At that point reconsider the long-lived stdin-pipe variant for that runtime — without changing the actor-serialized model.
- The `concurrent_threads: true` contract proves unteachable: authors keep violating it, the validator can't catch the violations statically, and runtime conflicts produce intermittent user-visible failures. At that point either narrow `true` to a tighter sub-mode (e.g. "stateless mode": no tool use allowed) or promote container-per-thread to `true`'s default implementation.
- Per-thread containers (sub-issue (e)) ship in v0.2 and prove cheap enough that the in-process `true` mode loses its niche. At that point the modes collapse: `true` becomes "container-per-thread", `false` stays as the in-process serialized default.
