# 0065 — Agent memory architecture: typed memory, durable store canonical, coordination via threads + instruction-level authority

- **Status:** Accepted (2026-06-01) — converged with @savasp on [#2993](https://github.com/cvoya-com/spring-voyage/issues/2993). Decisions 1–3 and 5 are recorded here; **push-vs-pull context assembly ([#1301](https://github.com/cvoya-com/spring-voyage/issues/1301)) is deliberately left open**, and a shared mutable-state mechanism is deferred.
- **Date:** 2026-06-01
- **Related ADRs:** [0030](0030-thread-model.md) — thread model (participant-set identity, single `AgentMemory`) this record builds on; [0060](0060-participant-set-agent-api-and-structured-envelope.md) — participant-set agent API (the substrate for coordination-via-threads); [0059](0059-prompt-assembly-pipeline.md) — prompt-assembly pipeline (where the Platform-Contract clause lands); [0056](0056-tool-only-side-effects.md) — tool-only side effects (fundamental-core criterion for what the prompt advertises); [0053](0053-units-are-agents-and-one-way-delivery.md) — units are agents / one-way delivery (a unit can be a decision authority by convention).
- **Related docs:** [`docs/concepts/threads.md`](../concepts/threads.md); [`docs/architecture/messaging.md`](../architecture/messaging.md); [`docs/architecture/open-questions.md`](../architecture/open-questions.md) (push-vs-pull stays open); [`docs/developer/incident-reports/2026.06.01-magazine-first-edition.md`](../developer/incident-reports/2026.06.01-magazine-first-edition.md).
- **Related code:** `src/Cvoya.Spring.Dapr/Skills/{SvMemorySkillRegistry,SvMemoryHistoryRegistry}.cs`; `src/Cvoya.Spring.Dapr/Memory/EfMemoryStore.cs`; `src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs`; `src/Cvoya.Spring.AgentRuntimes/Launchers/ClaudeCodeLauncher.cs`.
- **Related issues:** [#2980](https://github.com/cvoya-com/spring-voyage/issues/2980) (incident), [#2993](https://github.com/cvoya-com/spring-voyage/issues/2993) (design consolidation), [#2984](https://github.com/cvoya-com/spring-voyage/issues/2984) (surface + contract; absorbs the closed #2987), [#2985](https://github.com/cvoya-com/spring-voyage/issues/2985) (resume + concurrency), [#2977](https://github.com/cvoya-com/spring-voyage/issues/2977) (workspace-volume wipe), [#2990](https://github.com/cvoya-com/spring-voyage/issues/2990) / [#2991](https://github.com/cvoya-com/spring-voyage/issues/2991) (message get + JSON content), [#1301](https://github.com/cvoya-com/spring-voyage/issues/1301) (push/pull — open), [#1292](https://github.com/cvoya-com/spring-voyage/issues/1292) / [#1293](https://github.com/cvoya-com/spring-voyage/issues/1293) (deferred), [#2994](https://github.com/cvoya-com/spring-voyage/issues/2994) (magazine-unit prose instantiation).

## Context

A real multi-agent unit run — a magazine editorial team, 722 messages over 73 minutes — produced its edition but **never terminated**; along the way agents forgot prior work and **disavowed their own earlier messages** ([#2980](https://github.com/cvoya-com/spring-voyage/issues/2980), [#2977](https://github.com/cvoya-com/spring-voyage/issues/2977)). The operator asked a direct question: across the ways an agent can carry state — session resume, local-volume files, and the `sv.memory.*` database — **what is the right way to maintain memory**, and how should a *team* of agents hold shared coordination state?

What actually exists today (verified against source):

- `sv.memory.*` is a durable, owner-scoped Postgres store with `kind` = `long_term` (cross-conversation) or `short_term` (thread-scoped working notes); search is Postgres full-text (no embeddings). **But the platform system prompt advertises only the shared-history tools — the durable private CRUD surface is unadvertised** (audit finding F1).
- The **message log is already agent-queryable** (`sv.memory.history_with` / `engagements` / `search_messages`) — server-stamped, immutable, participant-set keyed.
- Native Claude Code **file memory is on the disposable workspace volume** (wiped on reclaim; `--resume` reloads only the transcript, with no memory injection) and collides under concurrent threads.
- **There is no shared cross-member store.** `sv.memory.*` is strictly owner-scoped; `IUnitStateCoordinator` is operator-tier config, not a runtime blackboard. Threads (ADR-0030 / ADR-0060) are participant-set timelines: append-only, multi-writer.

The failure was not the absence of a single store. It was (a) no *reliable* durable memory the platform actually routes agents to, and (b) no model for how a team converges on shared decisions.

## Decision

### 1. Memory is *typed*, not one slot — map each type to a system-of-record

| Memory type | Question it answers | System-of-record |
|---|---|---|
| Conversational continuity | "what were we just saying" | **session resume — a cache**, never the durable store |
| Episodic / attribution | "what did I / we actually say" | **the message log** (`history_with` / `search_messages` / `get_messages`) — authoritative |
| Semantic / decisions / working notes | "what did we decide / learn; my notes" | **`sv.memory.*`** (canonical durable per-agent store) |
| Fit-to-context | "what do I see this turn" | push/pull assembly — **open (#1301)** |

Corollary: **never trust an agent's self-report over the message log.** The disavowal failures were an agent contradicting messages it had genuinely sent; the log is the incorruptible ground truth and the cheap fix.

### 2. Keystone

`sv.memory.*` is the **canonical durable cross-thread store**. Native file memory is **per-session scratch** — best-effort, not relied on for cross-thread or cross-lifecycle state.

### 3. Surface + contract the durable store

The platform advertises durable memory through a **thin, always-pushed Platform-Contract clause** — *read relevant memory at the start of a turn; record decisions / completion before ending a turn* — with the concrete `sv.memory.*` tool surface auto-injected (not left to discovery). This resolves audit finding F1 (the now-closed #2987) and is implemented in #2984. Keeping the clause a thin behavioural pointer respects ADR-0056 §8's fundamental-core criterion.

### 4. Team coordination uses threads + instruction-level authority — **not** a shared-state primitive

- A thread is a participant set (ADR-0030 / ADR-0060): **append-only and multi-writer** — any participant may post. There is no "location" to overwrite, so there is **no write-write conflict, no lock, and no compare-and-set** at the storage layer.
- **Authority is an instruction-level convention, not a storage constraint.** Each agent's prompt states *whose decision stands*. Decisions that everyone must act on are **advertised** to the relevant participant set (e.g. the whole-team thread); members watch it and treat advertised decisions as binding. Convergence is social, not mechanical.
- **No shared mutable-state primitive in v0.2.** It is deferred until a concrete forcing case demands it. If leaderless, mergeable shared structures are ever genuinely needed, the answer is **CRDTs** (lock-free) — explored separately — not platform locks.

This is the lever that ended the incident's non-termination at the *design* level: a finished conversation needs a durable, advertised "we're done" decision (store + log + prompt-level authority), not a delivery-layer guard.

### 5. Record before implementing

This ADR is the gating artifact: the surfacing/contract work (#2984) and the resume/durability work (#2985 / #2977) implement decisions 1–4; they follow this record.

## Open and deferred

- **Open — push vs pull context assembly (#1301):** how durable memory, the message log, and advertised decisions reach a turn's context, including whether the platform pushes a rolling digest (important for small-context models). Deliberately undecided here.
- **Deferred (forcing-case-gated):** a shared mutable-state mechanism / CRDTs (separate exploration); cross-thread reads, unit recursion, and multi-human permission gating (#1292); inference provenance / memory hypergraph (#1293); semantic / vector search over `sv.memory.*`.

## Consequences

- File memory is demoted to scratch; #2977 still must land so scratch and `--resume` transcripts survive mid-run; #2985 narrows to in-session-scratch durability + write-collision safety.
- A unit agent can be a *decision authority* by convention (units-are-agents, ADR-0053), but only a **reliable** one once its durable memory lands (#2984) — the incident's unit agent disavowed its own rulings because its sessions were wiped.
- Multi-agent units coordinate by advertising decisions to participant-set threads under prompt-level authority. This is validated first **in prose** by the magazine unit (#2994), before any platform mechanism — a real test of the model.
- **Rejected:** a mutable shared "blackboard" store with locks. It imports deadlock, lock-ordering, and contention, and is unnecessary: append-only threads remove write-write conflict, and "who decides" is an instruction-level convention. (This reverses an earlier framing in #2993 that floated a single-writer shared store; @savasp's correction — multi-writer thread + prompt-level authority — is the accepted shape.)
