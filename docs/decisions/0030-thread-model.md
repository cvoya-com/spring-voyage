# 0030 — Thread model: participant-set identity, single AgentMemory, per-thread visibility policy

- **Status:** Accepted (2026-04-29). Supersedes [0018 — partitioned mailbox](archive/0018-partitioned-mailbox.md). v0.1 work.
- **Date:** 2026-04-29
- **Related code:** *(none yet — code rename to follow per [#1287](https://github.com/cvoya-com/spring-voyage/issues/1287))*
- **Related docs:** [`docs/architecture/thread-model.md`](../architecture/messaging.md) (long-form F1 design); [`docs/glossary.md`](../glossary.md).
- **Related ADRs:** [0026 — per-agent container scope](0026-per-agent-container-scope.md); [0029 — tenant execution boundary](0029-tenant-execution-boundary.md).
- **Issues:** [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) (the reframing); [#1268](https://github.com/cvoya-com/spring-voyage/issues/1268) (F1 system design); [#1273](https://github.com/cvoya-com/spring-voyage/issues/1273) (this ADR).

## Context

The prior model treated a *conversation* as a chat-style container the user creates and navigates: a "new conversation" button, a list of past conversations, an active-conversation slot inside the agent's mailbox. That shape was inherited from chat products and never fit the platform's actual capabilities — addressable agents that remember across encounters, multi-participant exchanges with humans and units, agents that may take initiative.

Two operational pain points kept surfacing. The agent mailbox conflated message arrival with execution serialisation ([#1085](https://github.com/cvoya-com/spring-voyage/issues/1085)): the single active-conversation slot meant a slow turn on one thread head-of-line-blocked every other thread that agent participated in. The UX pushed work-categorisation onto the user ([#1086](https://github.com/cvoya-com/spring-voyage/issues/1086)): "which conversation does this belong in?" is a question only the chat-container metaphor needed to ask. Both were symptoms of a model that was wrong rather than rough.

[#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) captured the reframing — *the thread is the participant set, not a chat container*. F1 ([#1268](https://github.com/cvoya-com/spring-voyage/issues/1268)) settled the design across ten specific questions in [`docs/architecture/thread-model.md`](../architecture/messaging.md). This ADR makes the architectural shape durable; the F1 doc carries the per-question rationale.

## Decision

- **Identity = the participant set.** A thread is uniquely identified by its set of two-or-more participants. There is exactly one thread per unique participant set; adding or removing a member produces a different set, hence a different thread. The thread is the canonical lifelong record for that set.
- **Membership is permanent; participant state is per-(thread, participant).** Each `(thread, participant)` pair has a state machine `added → active → removed → re-added`. Re-add does not create a new thread. A removed participant does not see new activity until re-added; the platform enforces a blackout window via a read-time Timeline filter.
- **Three terms with clean separation.** **Thread** is the system / architectural concept (used in code, schema, APIs); **Engagement** is the UX product narrative (the enduring relationship surface in navigation); **Collaboration** is the UX active workspace (where the user works today). The system stores a thread; the product presents it as an engagement; the user works inside a collaboration.
- **One container per agent**, unchanged from [ADR-0026](0026-per-agent-container-scope.md). An agent processes its threads concurrently by default, governed by a `concurrent_threads: bool` flag on the agent / unit definition (default `true`); per-thread FIFO is preserved, concurrency is across distinct threads only. Auto-batching is not imposed: the platform delivers one message per `on_message` invocation; the SDK exposes a `peek_pending(thread_id)` accessor for agents that want to drain.
- **Single per-agent `AgentMemory` with a per-thread visibility policy.** Each agent has one ordered, append-only memory store. Entries are `MemoryEntry` records of shape `{ id, timestamp, payload, thread_id?, threadOnly? }`. Visibility is a recall-time filter governed by the thread's **`ThreadMemoryPolicy`** (default `threadOnly: true`). The MCP tool surface collapses to **`store(memory)`** and **`recall(query)`**; the platform stamps `thread_id` and `threadOnly` from the agent's operating context.
- **Tasks are not a typed platform concept.** A task is a memory entry whose payload represents a task; lifecycle (created, updated, cancelled, completed) is expressed as further append-only entries. There are no `task.*` MCP tools, no `task_visibility` policy axis, no separate task store. The collaboration surface owns the task-panel rendering convention.
- **Thread Timeline is the canonical shared record.** Per thread, append-only, ordered, timestamped artifacts: **Message** (user / agent / initiative), **`ParticipantStateChanged`**, **Retraction**, system events. The per-participant view is a read-time filter, not a separate copy. Per-thread FIFO is the ordering invariant, on top of which [ADR-0029](0029-tenant-execution-boundary.md)'s A2A 0.3.x wire model rides.
- **Initiative messages are normal messages.** Optional `context` UX-hint metadata (`{ kind, task?, originating_message? }`) lets the surface render task-update / reminder / observation cues; the platform does not branch on `context`.
- **Correction is a normal user message.** The platform supplies one primitive — `message.retract` — for the agent to mark its own previously-sent message as retracted on the Timeline (soft, append-only). Cancelling work or re-scoping a task is the agent updating its memory entries via `store(memory)`. A collaboration affordance pre-templates the user's "this isn't what I meant" message.
- **Cold start is not a platform concept.** Empty-state UX is owned by E2; agent-side first-message behaviour is the agent runtime's prompt mechanism. No `instructions.opening_offer` field, no `is_first_contact` SDK hint.
- **No migration to v0.1.** No live deployment to preserve; v0.1 takes full schema / API freedom. Future migrations follow the public Web API versioning + deprecation policy.

## Alternatives considered

- **Keep the prior thread-as-session model.** Rejected: it conflates user-side work-categorisation with platform-side dispatch. [#1085](https://github.com/cvoya-com/spring-voyage/issues/1085) and [#1086](https://github.com/cvoya-com/spring-voyage/issues/1086) are symptoms of the model, not isolated bugs to patch.
- **Two-store memory (`AgentMemory` + `AgentThreadMemory` with promotion).** Considered in an early F1 draft. Rejected: it conflates "where to store" with "what should be visible later." The single-store + visibility-attribute model collapses both into one decision the operator's policy makes.
- **Tasks as a typed platform concept (`task.create` / `task.cancel` / `task.revise` MCP tools).** Considered in an early F1 draft. Rejected: once everything is a memory entry, tasks need no separate API, no separate visibility axis, no separate lifecycle vocabulary. The UX layer keeps the task concept; the platform layer doesn't.
- **Subset memory access** (T2 ⊂ T1 → agents in T2 read T1's thread-scoped memory). Considered and rejected during F1 review: it sidesteps `ThreadMemoryPolicy` and re-introduces the leakage the policy is designed to prevent. Continuity across threads goes through the standard `threadOnly: false` mechanism or explicit user reference.
- **Cold start as a platform field** (`instructions.opening_offer`, `is_first_contact` SDK hint). Considered in an early F1 draft. Rejected: cold-start behaviour varies by context, and agent runtimes (Claude Code's `~/.claude/CLAUDE.md`, OpenAI Assistants, etc.) already have prompt mechanisms. A platform field would either be too rigid or too vague.

## Consequences

### Simpler

- No "active conversation slot" gate (concurrent threads default); no head-of-line blocking across an agent's threads.
- No two-store / promotion API, no `task.*` MCP tools, no cold-start fields. The MCP surface collapses to `store`, `recall`, `message.retract`, `peek_pending`.
- The Timeline is the single shared concept covering messages, participant state changes, retractions, and system events; per-participant views are read-time filters, not copies.

### Harder

- The per-(thread, participant) state machine + Timeline visibility filter has to be implemented carefully; the boundary semantics at the moment of removal are subtle (strict-less-than against state-change timestamps). The long-form design doc carries the precise rule and pseudocode.
- The Engagement-as-stitching projection across distinct threads (when membership-set changes create a new thread) is a UX concept that has no persisted entity at the API surface; E2 owns the affordance.

### Policy-governed

- Memory visibility across threads is governed by per-thread `ThreadMemoryPolicy`. Default `threadOnly: true` (entries do not leave their thread). The tenant / unit / agent resolution chain is deferred; for v0.1 the policy lives on the thread.

### What this implies

- **Migration.** None. v0.1 ships clean. Pre-v0.1 internal data is the developers' problem.
- **Code rename:** `Conversation*` → `Thread*` symbols across the codebase (Area G; [#1287](https://github.com/cvoya-com/spring-voyage/issues/1287)).
- **Public API:** `/api/v1/threads/{id}` is the canonical URL surface; no `/conversations` alias ([#1291](https://github.com/cvoya-com/spring-voyage/issues/1291)).
- **CLI:** `spring conversation …` → `spring thread …` ([#1288](https://github.com/cvoya-com/spring-voyage/issues/1288)).
- **Doc sweep:** `messaging.md`, `units.md`, `agent-runtime.md`, glossary entries land in F2 ([#1271](https://github.com/cvoya-com/spring-voyage/issues/1271)).
- **Deferred for future design:** unit recursion, cross-thread reads at the recall API level, multi-human permission gating ([#1292](https://github.com/cvoya-com/spring-voyage/issues/1292)); inferences with explicit provenance ([#1293](https://github.com/cvoya-com/spring-voyage/issues/1293)).
- **Thread-close vestige removal ([#2074](https://github.com/cvoya-com/spring-voyage/issues/2074)).** The legacy `threads.status` column, `POST /api/v1/threads/{id}/close` endpoint, `IAgentActor.CloseThreadAsync`, `spring thread close` CLI verb, and the `ThreadClosed` activity event were vestiges of the pre-#1268 chat-container metaphor that this ADR supersedes; they have been deleted in #2074. The legitimate "I'm done with this thread" semantic is a per-(thread, participant) `ParticipantStateChanged` transition to `removed`.

### ADR-0018 superseded

The control-channel and observation-channel partitioning from [ADR-0018](archive/0018-partitioned-mailbox.md) carries forward unchanged — control still pre-empts work; observations still arrive as a batched digest. What this ADR replaces is the conversation-channel single-active-slot semantics: per-thread FIFO holds inside each thread, but threads run concurrently by default per the `concurrent_threads` flag.
