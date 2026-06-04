# 0065 — Agent memory architecture: typed memory, durable store canonical, coordination via threads + instruction-level authority

- **Status:** Accepted (2026-06-01) — converged with @savasp on [#2993](https://github.com/cvoya-com/spring-voyage/issues/2993). Decisions 1–3 and 5 are recorded here; **push-vs-pull context assembly ([#1301](https://github.com/cvoya-com/spring-voyage/issues/1301)) is deliberately left open**, and a shared mutable-state mechanism is deferred.
- **Date:** 2026-06-01
- **Related ADRs:** [0030](0030-thread-model.md) — thread model (participant-set identity, single `AgentMemory`) this record builds on; [0060](0060-participant-set-agent-api-and-structured-envelope.md) — participant-set agent API (the substrate for coordination-via-threads); [0059](0059-prompt-assembly-pipeline.md) — prompt-assembly pipeline (where the Platform-Contract clause lands); [0056](0056-tool-only-side-effects.md) — tool-only side effects (fundamental-core criterion for what the prompt advertises); [0053](0053-units-are-agents-and-one-way-delivery.md) — units are agents / one-way delivery (a unit can be a decision authority by convention).
- **Related docs:** [`docs/concepts/threads.md`](../concepts/threads.md); [`docs/architecture/messaging.md`](../architecture/messaging.md); [`docs/architecture/open-questions.md`](../architecture/open-questions.md) (push-vs-pull stays open); [`docs/developer/incident-reports/2026.06.01-magazine-first-edition.md`](../developer/incident-reports/2026.06.01-magazine-first-edition.md).
- **Related code:** `src/Cvoya.Spring.Dapr/Skills/{SvMemorySkillRegistry,SvMemoryHistoryRegistry}.cs`; `src/Cvoya.Spring.Dapr/Memory/EfMemoryStore.cs`; `src/Cvoya.Spring.Dapr/Prompts/PlatformPromptProvider.cs`; `src/Cvoya.Spring.AgentRuntimes/Launchers/ClaudeCodeLauncher.cs`.
- **Related issues:** [#2980](https://github.com/cvoya-com/spring-voyage/issues/2980) (incident), [#2993](https://github.com/cvoya-com/spring-voyage/issues/2993) (design consolidation), [#2984](https://github.com/cvoya-com/spring-voyage/issues/2984) (surface + contract; absorbs the closed #2987), [#2985](https://github.com/cvoya-com/spring-voyage/issues/2985) (resume + concurrency), [#2977](https://github.com/cvoya-com/spring-voyage/issues/2977) (workspace-volume wipe), [#2990](https://github.com/cvoya-com/spring-voyage/issues/2990) / [#2991](https://github.com/cvoya-com/spring-voyage/issues/2991) (message get + JSON content), [#1301](https://github.com/cvoya-com/spring-voyage/issues/1301) (push/pull — open), [#1292](https://github.com/cvoya-com/spring-voyage/issues/1292) / [#1293](https://github.com/cvoya-com/spring-voyage/issues/1293) (deferred), [#2994](https://github.com/cvoya-com/spring-voyage/issues/2994) (magazine-unit prose instantiation), [#2997](https://github.com/cvoya-com/spring-voyage/issues/2997) (rename the memory scope axis: `kind`→`scope`).

## Context

A real multi-agent unit run — a magazine editorial team, 722 messages over 73 minutes — produced its edition but **never terminated**; along the way agents forgot prior work and **disavowed their own earlier messages** ([#2980](https://github.com/cvoya-com/spring-voyage/issues/2980), [#2977](https://github.com/cvoya-com/spring-voyage/issues/2977)). The operator asked a direct question: across the ways an agent can carry state — session resume, local-volume files, and the `sv.memory.*` database — **what is the right way to maintain memory**, and how should a *team* of agents hold shared coordination state?

What actually exists today (verified against source):

- `sv.memory.*` is a durable, owner-scoped Postgres store; search is Postgres full-text (no embeddings). It carries a `kind` = `long_term` / `short_term` axis (short-term tagged with a `thread_id`, descending from ADR-0030's per-entry `thread_id` / `threadOnly` model). The axis is real — it is a *scope* distinction — but the `long_term`/`short_term` names mislead: both are durable (see Decision 3). **The platform system prompt advertises only the shared-history tools — the durable private CRUD surface is unadvertised** (audit finding F1).
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
| Semantic / decisions / context (durable; agent- or thread-scoped) | "what did we decide / learn; my private notes for this conversation" | **`sv.memory.*`** (canonical durable per-agent store) |
| Fit-to-context | "what do I see this turn" | push/pull assembly — **open (#1301)** |

Corollary: **never trust an agent's self-report over the message log.** The disavowal failures were an agent contradicting messages it had genuinely sent; the log is the incorruptible ground truth and the cheap fix.

### 2. Keystone

`sv.memory.*` is the **canonical durable cross-thread store**. Native file memory is **per-session scratch** — best-effort, not relied on for cross-thread or cross-lifecycle state.

`sv.memory.*` keeps **two memory scopes** but **renames the axis** (#2997). The existing `kind = long_term | short_term` names a *lifetime* the data never had: a `short_term` entry is a durable row tagged with a `thread_id`; nothing evicts it when the thread ends, and a thread persists while any participant remains — so a thread-scoped memory lasts **as long as the agent exists**. The real axis is **scope**, not time:

- **agent-scoped** (default) — durable knowledge that applies across *all* the agent's conversations.
- **thread-scoped** — private notes that apply *only within one thread / participant set* (e.g. "in this thread, refer to agent XYZ as 'Bob'").

Thread-scoped memory is worth keeping and is distinct on both sides: it is **not** the thread's shared message log (a separate table — the log is what was *said*; this is the agent's private interpretation, which it would not broadcast), and it is **not** agent-scoped memory (which would leak the note into unrelated threads and pollute recall). Recommended terms: **`scope: agent | thread`** replacing `kind: long_term | short_term`; a thread-scoped entry carries the `thread_id` it is bound to. (Per-thread *visibility / permission* scoping — #1292 — is a separate policy axis, not this scope field.)

### 3. Surface + contract the durable store

The platform advertises durable memory through a **thin, always-pushed Platform-Contract clause** — *read relevant memory at the start of a turn; record decisions / completion before ending a turn* — with the concrete `sv.memory.*` tool surface auto-injected (not left to discovery). This resolves audit finding F1 (the now-closed #2987) and is implemented in #2984. Keeping the clause a thin behavioural pointer respects ADR-0056 §8's fundamental-core criterion.

> **Update (2026-06-02) — F1 fully closed.** #2984 delivered the behavioural clause but left the durable-store CRUD tools (`add`/`get`/`list`/`search`/`update`/`delete`) and `get_messages` **unnamed in the system prompt** — only the three shared-history tools were enumerated, so F1 was only partially closed and the durable surface was still effectively left to discovery (which agents did not perform). This change completes Decision 3: the platform-tool catalog now enumerates the **full** `sv.memory.*` surface, and the durable-memory clause both names the durable-store tools inline and *actively promotes* their use (recall at turn start; record before turn end; "when in doubt, record it"). The surface is therefore promoted into the in-prompt fundamental core — see the [ADR-0056](0056-tool-only-side-effects.md) §8 amendment of the same date.

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

- File memory is demoted to scratch. With the workspace-volume-wipe fix landed (#2999), `--resume` transcripts survive mid-run; #2985 then **disabled Claude Code's native auto-memory** (`CLAUDE_CODE_DISABLE_AUTO_MEMORY=1`, set in `ClaudeCodeLauncher`) so durable state has exactly one home (`sv.memory.*`) and concurrent threads cannot collide on a shared per-repo memory dir — superseding the earlier "serialize per-agent writes" framing and eliminating #2982 finding D structurally. The per-thread session transcript (keyed by `thread.id`) is governed by a separate knob and is unaffected, so it still carries within-thread conversational continuity.
- The `sv.memory.*` memory axis is **renamed** — `kind: long_term / short_term` → `scope: agent | thread` (#2997). Both scopes are durable; the misleading temporal terms are dropped. Thread-scoped entries are private notes bound to one participant set — distinct from the shared message log, and from agent-scoped memory.
- `sv.memory.*` content is a **structured JSON value** stored in `jsonb` (#2991): a plain note is a JSON string, structured state is an object/array, round-tripped type-preserved through `add` / `get` / `list` / `search` / `update` (Postgres `to_tsvector(jsonb)` makes structured entries searchable by their string values). Agents record structured working state — the "edition status board" — without hand-stringifying; there is no separate format discriminator (the JSON type is the discriminator). **(Amended 2026-06-02 — the single-tool `string | object` union is being replaced by typed `object` / `text` tool variants per the Amendment below ([#3038](https://github.com/cvoya-com/spring-voyage/issues/3038)); the `jsonb` storage and type-preservation are unchanged.)**
- A unit agent can be a *decision authority* by convention (units-are-agents, ADR-0053), but only a **reliable** one once its durable memory lands (#2984) — the incident's unit agent disavowed its own rulings because its sessions were wiped.
- Multi-agent units coordinate by advertising decisions to participant-set threads under prompt-level authority. This is validated first **in prose** by the magazine unit (#2994), before any platform mechanism — a real test of the model.
- **Rejected:** a mutable shared "blackboard" store with locks. It imports deadlock, lock-ordering, and contention, and is unnecessary: append-only threads remove write-write conflict, and "who decides" is an instruction-level convention. (This reverses an earlier framing in #2993 that floated a single-writer shared store; @savasp's correction — multi-writer thread + prompt-level authority — is the accepted shape.)

## Amendment (2026-06-02) — second magazine dogfood run

A larger magazine run (452 messages) stress-tested this architecture and surfaced two refinements. Both emerged from a confabulation-cascade analysis ([#3034](https://github.com/cvoya-com/spring-voyage/issues/3034)): agents disavowed real, correctly-attributed messages and deadlocked the unit on a false "fabricated messages" alarm — the same disavowal failure mode as the 2026-06-01 incident, recurring at larger scale (platform integrity was intact: 0 unresolvable senders across 452 messages, 1 error event in 6,183 activities).

**1. Tool contract — replace the `string | object` content union with typed variants (revises the JSON-content consequence above).** The `jsonb` store and type-preservation hold. But "one tool, JSON-type-as-discriminator" proved fragile: agents stored content *both* as JSON objects *and* as stringified-JSON strings, inconsistently, because the union permitted both. Decision: split each verb into typed variants, **object-primary** — `sv.memory.add(object)` + `sv.memory.text.add(string)` (and likewise `update` / `get` / `search`) — since agents overwhelmingly *chose* structured objects unprompted, and object entries enable field-level `search`. The `text.*` variant stores a JSON string (no storage change). Tracked in [#3038](https://github.com/cvoya-com/spring-voyage/issues/3038).

> **Amendment (2026-06-04) — re-merge the union, remove the `.text` variants ([#3064](https://github.com/cvoya-com/spring-voyage/issues/3064) / [#3065](https://github.com/cvoya-com/spring-voyage/issues/3065)).** The typed split traded one failure mode for another: keeping both `add` and `text.add` on the generated system prompt re-presented the very add-vs-text-add choice the split was meant to remove, and the model fumbled it (≈8× in a live run). Decision: re-merge into a single **forgiving** `sv.memory.add` / `sv.memory.update` that accept `text | object` (any JSON type) in one `content` argument and store it type-preserved, and **remove** `sv.memory.text.add` / `sv.memory.text.update` entirely (v0.1 aggressive cleanup, no back-compat). `jsonb` storage and type-preservation are still unchanged; `get` / `list` / `search` never had `.text` variants, so they are untouched.

**2. Thread-scope (Decision 2) went entirely unused.** All 100 durable entries in the run were agent-scoped; no agent used `scope: thread`. Decision 2 still stands, but the run shows agents need *guidance* to reach for it — and there are concrete cases where it helps: per-conversation state for high-fan-out roles, and a per-thread truth anchor that would resist the cross-thread confabulation in #3034. Whether/how to guide agents toward thread-scope is tracked in [#3037](https://github.com/cvoya-com/spring-voyage/issues/3037).
