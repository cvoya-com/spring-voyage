# Thread Model — Participant-Set Design

> **[Architecture Index](README.md)** | **F1 design output for v0.1.** Owned by Area F. Pointers: [`docs/plan/v0.1/areas/f-conversation.md`](../plan/v0.1/areas/f-conversation.md), [#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) (the reframing), [#1268](https://github.com/cvoya-com/spring-voyage/issues/1268) (this issue), [ADR-0029](../decisions/0029-tenant-execution-boundary.md) (the SDK / container boundary this design must respect), [ADR-0018](../decisions/0018-partitioned-mailbox.md) (the mailbox decision this design supersedes — F2 owns the supersession PR). Renamed from `conversation-model.md` in the same PR that landed the Thread / Engagement / Collaboration terminology.

---

## Framing — the participant-set invariant

The model uses three terms with clean separation:

- **Thread** — the system / architectural concept. **The unique, persistent, system-level record for a set of two or more participants, containing their lifelong shared exchanges and activity.** The participant set IS the identity: there is exactly one thread for each unique participant set. Adding or removing a participant produces a different participant set, hence a different thread.
- **Engagement** — the product / UX narrative term. **The ongoing shared context between participants over time.** This is the enduring relationship surface — the thing a user navigates to when they want to "continue with their writing agent." An engagement absorbs participant-set transitions (see Q6) so that adding or removing a participant does not fragment the user's mental model.
- **Collaboration** — the active workspace / UX surface where work happens. **The active shared space where participants converse, coordinate, and get work done.** This is what the user opens to do something today.

A user opens a **collaboration** to work; the system records it under the relevant **thread**; they revisit it later as one of their **engagements**.

Examples of the participant-set invariant:

- `{Savas, Agent A}` → one thread.
- `{Savas, Agent A, Agent B}` → a different thread.
- `{Savas, Alice}` → one thread; `{Alice, Savas}` → the same thread (the set is identical).

A participant may belong to many threads.

Every thread has an ordered, timestamped **Timeline** of all artifacts it accumulates — messages (user / agent / initiative), participant state changes, retractions, and system events. The Timeline is append-only at the platform level; it is the canonical record of what happened in the thread, in what order. (Tasks are *not* Timeline artifacts — they are memory entries on each agent's `AgentMemory`; see Q5.) Per-question detail belongs in Q7.

Each agent has a single **`AgentMemory`** — an ordered, append-only memory store. Entries are **`MemoryEntry`** records with optional `thread_id` and `threadOnly` attributes; per-thread visibility is enforced at the recall filter, governed by the thread's **`ThreadMemoryPolicy`**. Detail belongs in Q4.

A thread's identity is its permanent membership set, but per-thread state may transition for individual participants (`added → active → removed → re-added`); a removed participant does not see new activity until re-added. Detail belongs in Q6.

This document answers the ten F1 design questions that have to settle before F2 (doc revisions), F3 (the new ADR), and the F execution plan can begin.

---

## 1. Naming

**Decision:** **Thread** (internal / architectural / system) / **Engagement** (UX product narrative) / **Collaboration** (UX active workspace). The system stores a thread; the product presents it as an engagement; the user works inside a collaboration.

**Rationale.**

- A clean three-tier semantic split — system / product / working UI — beats overloading one word across all three layers. "Conversation" has been carrying that load and silently bending under it: code uses it for the participant-set relationship, docs use it for the user-visible surface, the product layer needs a warmer word for the enduring connection. One term cannot carry three jobs without one of them suffering.
- The mapping is direct and load-bearing: the kernel's identity (a unique participant set with a lifelong record) is exactly a **Thread**; the relationship the user has with the participants over time is an **Engagement**; the active space they open to do something is a **Collaboration**. Each term names the layer it belongs to and only that layer.
- "Conversation" stays meaningful in informal prose ("they had a conversation about X") but is no longer a typed concept in the system, the product, or the working UI. The overload is what we are eliminating.
- The cost (sweep across `messaging.md` / `units.md` / `agent-runtime.md` plus the code-level rename of `Conversation*` types) is real, but bounded and one-shot. We pay it once during v0.1 to get terminology that the next ten years of the platform can lean on without surfacing the same confusion every time someone reads the code or the docs.

### Canonical definitions

Use these verbatim where possible (also in the glossary):

> **Thread**: the unique, persistent, system-level record for a set of two or more participants, containing their lifelong shared exchanges and activity.

> **Engagement**: the ongoing shared context between participants over time.

> **Collaboration**: the active shared space where participants converse, coordinate, and get work done.

### Practical guide — where to use which term

| Layer | Term | Example phrasing |
| --- | --- | --- |
| Code, schema, architecture, APIs | **Thread** | "Find the thread", "Append to the thread", "Thread lookup is keyed by normalized participant set" |
| Product navigation / lists | **Engagement** | "Recent engagements", "Continue this engagement", "This engagement has new activity" |
| Active workspace / main screen | **Collaboration** | "Open collaboration", "Resume collaboration with your writing agent", "This collaboration has 3 participants" |

### Engagement vs. Collaboration — the UX split

- **Engagement** = the enduring connection. Use for relationship narrative — "the ongoing shared journey", continuity over time, something broader and warmer than messaging.
- **Collaboration** = the active manifestation. Use for active teamwork, tasks/decisions/plans/execution, the space the user opens and works in.

A user opens a **collaboration** to work; the system records it under the relevant **thread**; they revisit it later as one of their **engagements**.

**Consequences.**

- F2's doc-revision pass sweeps `Conversation*` → `Thread*` across `docs/architecture/messaging.md`, `docs/architecture/units.md`, `docs/architecture/agent-runtime.md`, and the glossary. The glossary entries for **Thread** / **Engagement** / **Collaboration** ship in this PR (alongside this doc) so F2 has the canonical anchors to point at.
- A code-level rename follows: `IConversationQueryService`, `ConversationId`, `Message.ConversationId`, the activity-event projection's `CorrelationId == ConversationId` mapping, and every repository / actor / test that mentions `Conversation*`. This is Area G / D scope, not F1's; flagging here so the cost is visible.
- The CLI's `spring conversation …` verb becomes `spring thread …` (Area E1's call to schedule).
- The current public Web API exposes `/api/v1/conversations`. The new canonical surface is `/api/v1/threads`. There is no `/conversations` alias — Q10 settles that there is nothing to be backwards-compatible with. C2 owns the URL story.
- This file was renamed from `conversation-model.md` to `thread-model.md` in the same PR that landed this terminology decision. Anyone with an older link should follow the redirect; the architecture index already points at the new path.
- The cost is real and load-bearing — a sweep across multiple architecture docs plus a code rename — but the terminology clarity wins are worth it. Paying the cost once now beats paying interpretation cost every time someone reads "conversation" and has to decide which of three meanings is in play.

---

## 2. Container / execution model

**Decision:** **One container per agent**, as ADR-0029 + ADR-0026 already establish. A single agent container handles every thread that agent participates in. We do **not** introduce per-thread containers, and we do not introduce a capped pool keyed on thread.

**Rationale.**

- ADR-0029's whole point is that the platform/tenant boundary is per-agent: the agent owns its persistent volume, its A2A endpoint, its MCP session, its identity. Keying containers per-thread would either (a) duplicate that boundary N times per agent (one volume per thread, one A2A endpoint per thread) or (b) leave it per-agent but spawn ephemeral inner workers per thread, which is just "the agent's internal dispatch model" — an implementation choice for the agent, not a platform primitive.
- Per-thread containers blow up the cost model. An agent participating in 50 engagements would warm 50 containers, each holding its own LLM client, its own tool cache, its own MCP session. The whole reason ADR-0026 picked per-agent was that per-unit was wrong for the same reason in the other direction — too coarse — and per-thread is much further past the optimum.
- The cold-start story is fine under per-agent. The first message to a (participant-set, agent) pair is the first message **to that agent for that thread** — but the agent itself is already warm if it is being spoken to at all. The agent loads per-thread memory from its workspace volume on the first turn (the dispatcher passes the thread id in the A2A message; the SDK looks it up); cold-start cost is a per-thread memory hydration, not a container start. That is well under a second for any reasonable per-thread memory shape.
- Isolation between threads becomes the agent's responsibility, not the platform's. That is the right place for it: the agent already has to compose Layer-3 prompt context from the right thread's history (today via `ConversationId` lookup, renamed to `ThreadId` post-rename); it already has to decide how to organise its workspace volume across threads. The platform giving each thread its own container would not actually isolate anything the agent cared about — the agent's memory model spans threads by design (Q4) and the LLM call is the same call.
- Per-thread containers would also break the Q3 dispatch story: threads need to interleave on the same agent (control channel pre-empts work; pending-thread queue is at the agent level), which requires the agent to be the unit that schedules across them.

**Consequences.**

- ADR-0029's "per-agent persistent volume" gains an explicit per-thread sub-structure expectation. The platform stays opaque to volume contents, but the SDK contract (Bucket 1) needs to standardise the thread-id field on `on_message(message)` so the agent's per-thread memory hydration is keyed consistently. The exact field shape is a D1 deliverable.
- We confirm (not change) ADR-0026 and ADR-0029. F3's new ADR cites both rather than re-litigating either.
- The **multi-party** case (a unit `U` with many member agents in a thread `{user, U}`) is not affected: the unit is itself an agent in container terms, and U's orchestration strategy decides which member processes which message — exactly as today.
- **Web Portal continuity:** unchanged. The portal already does not address containers.
- **Hosted-service foundation:** unchanged — per-agent is the cheaper hosted model.

---

## 3. Per-thread dispatch semantics

**Decision:** The platform delivers **one message per `on_message` invocation**. Multiple queued messages on the same thread are **not** auto-batched by the platform; the agent's SDK exposes a `peek_pending(thread_id)` accessor (mirroring the existing `checkMessages` tool) so the agent **chooses** whether to drain pending messages into the current turn or process them sequentially. Default agent behaviour, where an SDK provides a default, is sequential — one-message-per-turn — with the agent free to opt into batching.

**Sub-decision — concurrent threads per agent.** An agent processes **multiple threads concurrently by default.** An agent participating in N threads may have up to N `on_message` calls in flight simultaneously — one per thread. Per-thread FIFO is preserved within each thread; concurrency is across distinct threads only. The agent / unit definition carries a **`concurrent_threads: bool`** field (default **`true`**). When set to `false`, the agent processes threads serially: at most one `on_message` call is in flight across all threads the agent participates in.

**Rationale.**

- Auto-batching sounds like a free latency win but it shapes agent reasoning in a way the platform should not impose. An agent that is mid-tool-call for message N may want to finish before reading N+1 (correctness); a chat-shaped agent may want to immediately collapse N+1 into the working context (latency); an LLM doing structured planning may want to read all pending messages once and re-plan (token efficiency). The right answer differs per agent. The platform should not pick.
- One-per-invocation matches the A2A wire model in ADR-0029 (one A2A request per inbound message; the agent's response may stream). Auto-batching would either invent a non-A2A bulk message shape or fold N user turns into one A2A request, both of which fight the protocol.
- An agent peeking the queue at a checkpoint is exactly the existing `checkMessages` semantics from ADR-0018 / `messaging.md`. Lifting it to a first-class SDK accessor formalises a pattern delegated agents already use; it is a small surface, not a new mechanism.
- Ordering is preserved: per-thread messages dequeue in arrival order, FIFO. The platform does not promise causal ordering across threads (different participant sets can race) but does promise per-thread FIFO, which is the only ordering an agent reasoning about "what just happened in this thread" can sensibly use.
- Suspension semantics from ADR-0018 (an agent can suspend the active thread, run another, resume) carry forward unchanged. Suspension is at thread-grain, which is the right grain for "I am blocked on user input here, let me work on something else."
- Concurrent-threads-by-default reflects the common case: most agents handle independent threads independently and benefit from doing work in parallel rather than head-of-line blocking on a single slow turn. The `concurrent_threads: false` opt-out covers the agents that actually need serialization — resource-bound agents, LLM-context-bound agents that cannot multiplex cleanly, agents with strict ordering needs across threads. Making this a definition-time toggle (not a per-message flag) keeps the dispatch contract simple: the runtime knows the answer at the agent level.

**Consequences.**

- D1's `on_message` contract carries a thread-identifying field, `message_id`, and a `pending_count` hint (cheap signal that more messages are queued — agent decides what to do with it). The on-the-wire field name is `thread_id` (no `conversation_id` alias — Q10 settles that there is no migration to perform).
- The SDK / runtime must explicitly support **re-entrancy of `on_message` across distinct `thread_id` values** — D1's contract documents this explicitly. The `concurrent_threads: bool` flag on the agent / unit definition controls whether the runtime allows that re-entrancy.
- The control-channel / observation-channel model from ADR-0018 stays as-is: control still pre-empts thread work; observations still arrive as a batched digest. (These channel names refer to mailbox channels, not to the Thread / Engagement / Collaboration trio; they are unaffected by Q1.) The Q3 decision is about per-thread message-channel shape only.
- F2 updates `messaging.md` to clarify that "active thread" semantics are unchanged but that the per-thread FIFO contract is now a documented promise (not just an emergent property of the actor turn model), and to document the `concurrent_threads` agent-definition flag.

---

## 4. Memory model

**Decision:** A single per-agent memory store with per-entry visibility attributes, one per-thread policy, one safe default.

### Single store

- Each agent has **one `AgentMemory`** — the agent's complete memory store. There is no separate `AgentThreadMemory` type, store, or concept.
- `AgentMemory` is structurally an ordered, append-only sequence of **memory entries** (`MemoryEntry`). Each entry has the shape:

  ```
  { id, timestamp, payload, thread_id?, threadOnly? }
  ```

  - `id` — entry identifier.
  - `timestamp` — when the entry was created.
  - `payload` — the artifact content. May be a fact, a lesson, a generalised pattern, an observation, a reference to a thread event, an inferred conclusion — any **memory artifact**. (See artifact uniformity below.)
  - `thread_id` — nullable. Set when the entry was created in a thread context; null for thread-less entries (e.g., platform-generated agent-definition activity).
  - `threadOnly` — only meaningful when `thread_id` is set. Stamped from the thread's **`ThreadMemoryPolicy`** at write time. `true` = the entry is visible to this agent only when operating in this thread. `false` = the entry is visible to this agent across all of its threads.

- "Per-thread memory" (the prior `AgentThreadMemory` framing) is now a **filter view** over `AgentMemory`: the subset of entries with `thread_id == T`. Not a separate store.

### Artifact uniformity

A "memory artifact" is the general term. **Lessons, facts, messages, tasks, reasoning steps, generalised patterns, and inferred conclusions are all memory artifacts** — there is no special "lesson" or "fact" type at the contract level. The platform stores them uniformly; the agent's cognition decides what each artifact represents.

### Policy

- **`ThreadMemoryPolicy`** — per-thread policy that sets the default `threadOnly` value for memory entries stored by an agent operating in that thread.
- **Default: `threadOnly: true`** (entries do not leave the thread).
- This is the operator's privacy / trust knob. It is the only memory-flow knob in v0.1.
- Per-thread setting; resolution chain (tenant → unit → agent → thread) deferred — for v0.1, the policy lives on the thread.

### MCP tool surface

Two tools, both implicit in the agent's current operating context (the platform knows which thread the agent is operating in via `IAgentContext`):

- **`store(memory)`** — appends a new memory entry to the agent's `AgentMemory`. Platform stamps:
  - `thread_id` from the agent's current operating thread (always set in v0.1; see "Forward-looking" below).
  - `threadOnly` from the thread's `ThreadMemoryPolicy`.
  - `id`, `timestamp`.

  The agent passes only the `memory` payload. Replaces the prior `learn` / `storeLearning` tool.

- **`recall(query)`** — reads the visible subset of the agent's `AgentMemory` for its current operating context. The visibility filter returns:
  - All entries with `thread_id == current_thread` (always visible to a participating agent).
  - All entries from other threads where `threadOnly == false`.
  - All entries with `thread_id == null` (thread-less, always visible).

  Replaces the prior `recall` / `recallMemory` tool.

### Reading vs. operating context

- The agent always reads its `AgentMemory` while operating in any thread; the visibility filter is the only restriction.
- The agent does **not** read another agent's memory of any kind.
- Cross-thread reads happen via the `threadOnly: false` mechanism — entries with `threadOnly: false` are visible across the agent's threads; entries with `threadOnly: true` are not.

### Cloning

- A clone is a new agent identity. At clone time, the clone receives a snapshot of the parent's `AgentMemory` (the entries plus their attributes — including `thread_id`, `threadOnly`).
- The clone does not participate in any threads at clone time; subsequent participation generates new entries with the new thread context.
- `ThreadMemoryPolicy` does not transfer (it lives on the thread, not the agent). When the clone joins a new thread, that thread's policy applies.
- Cloning is not blocked by policy.

### Authoring guidance

When designing or instructing an agent, the design intent is to make memory artifacts **as widely useful as the thread's policy allows**. The agent's cognition is the judge of cross-thread relevance — the platform does not impose. This is *guidance*, not enforcement; the policy remains the operator's privacy call, and `threadOnly: true` is the safe default.

| Artifact has cross-thread value? | Thread's `ThreadMemoryPolicy` | Stored as |
|---|---|---|
| Yes | `threadOnly: false` | Visible across the agent's threads after `store()` |
| Yes | `threadOnly: true` | Stored with `threadOnly: true`; not visible outside this thread (may surface later if policy is relaxed for new entries, but old entries keep their stamped value) |
| No | Either | Stored with whatever `threadOnly` the thread's policy dictates; the agent doesn't reason about cross-thread value |

Note on the third row: the agent's cognition decides cross-thread value; even if the policy permits cross-thread visibility, the agent should not reach for it for genuinely thread-local artifacts (a task scoped to this collaboration, a transient observation). Cross-thread visibility is not a default — it's earned by relevance.

### Forward-looking — what the schema reserves room for

The shape is designed to allow these future moves without restructuring `AgentMemory`:

- The `thread_id` field is nullable to allow thread-less entries from future inference operations (deferral 4 below) and from platform-generated agent-level activity (e.g., agent definition updates).
- The single-store / attribute-based shape allows future relationship-style metadata (e.g., `inferred_from`) to be added as additional optional fields without restructuring `AgentMemory`.

**Rationale.**

- The two-store model (`AgentMemory` + `AgentThreadMemory` with promotion between them) was conceptually clean but overweight: it required the agent to make a write-time choice about *which* store, which conflated "where to store" with "what should be visible later." The single-store + attribute model collapses these: the agent stores; the policy decides visibility; the recall filter enforces it. Smaller mental model, smaller API.
- Default `threadOnly: true` is the safe-by-default position: no information leaves a thread unless the operator explicitly opts in.
- Visibility-as-attribute (rather than store-membership) supports future enhancements cleanly: inferences with explicit provenance, thread-less entries, richer policy attributes — all become additional fields on `MemoryEntry` rather than new stores or new APIs.
- Layering matches ADR-0029's "memory deferred" hold: memory is **not** at the SDK boundary. `store` / `recall` are MCP tools the platform offers via the public Web API, not bucket-1 SDK hooks.

**Consequences.**

- F3's ADR ratifies the single-store memory model and `ThreadMemoryPolicy`.
- The renamed MCP tools (`store`, `recall`) replace `storeLearning` and `recallMemory` in the public Web API surface. C1 / C2 own the surface change.
- F2's doc revisions sweep `MemoryFlowPolicy` (prior draft term) and the now-retired `MemoryPromotionPolicy` (also prior draft term — never landed in code) → `ThreadMemoryPolicy`. The two-store framing in `messaging.md` / `units.md` is replaced with the single-store + attribute-based view.
- D1 sees a smaller surface: `store` and `recall` MCP tools, the `MemoryEntry` shape, the `ThreadMemoryPolicy` attached to threads.
- **Hosted-service foundation:** safe-by-default (`threadOnly: true`) + a single per-thread policy knob is exactly what a hosted service needs.

**Deferred from v0.1.** Four things this design intentionally leaves out:

1. **Unit recursion** — a unit reading its members' memory entries from threads the unit itself is not in.
2. **Cross-thread reads at the recall API level** — by any agent or unit. (The `threadOnly: false` mechanism is the only way memory crosses threads in v0.1.)
3. **Multi-human permission gating on memory access** — surface filtering based on the asking human's permissions on the source thread.
4. **Inferences with explicit provenance** — agent-driven thread-less entries that synthesise across multiple sources, with a relationship structure linking inferred entries to their source entries (a hypergraph). v0.1 has no inference operation; agents may produce derivative entries via `store(memory)` but cannot record that they were inferred from specific source entries. A separate follow-up issue captures the design space.

Each is a real concern for richer multi-human / multi-unit deployments (1–3) or for richer cognition models (4) and warrants design when there's a forcing case. Filed as a single follow-up.

---

## 5. Tasks

**Decision:** Tasks are **not a typed platform concept.** A task is a **memory entry** whose payload represents a task — by application or surface convention; the platform doesn't reify it. Task lifecycle (created, updated, cancelled, completed) is expressed as **additional memory entries** that the agent's cognition or the surface interprets as updates to the prior task entry. All Q4 rules — visibility via `ThreadMemoryPolicy`, the recall filter, cloning, append-only — apply to task entries uniformly.

**Consequences.**

- **No `task.create` / `task.update` / `task.cancel` / `task.revise` MCP tools.** The agent stores task-shaped payloads via `store(memory)` and updates them by storing further entries. Q4's tool surface (`store`, `recall`) is the only API.
- **No `task_visibility` policy axis** (already removed in the prior pass; reaffirmed here).
- **No "task store" / "agent task store" concept.**
- **The `#name` reference is a UX / surface convention.** It's a way for users to address a task-payload entry in the current thread; the surface resolves it. Cross-thread `#name` resolution remains deferred (#1292).
- **The collaboration surface still renders a task panel.** That's E2's job — interpret memory entries whose payload looks task-shaped and render lifecycle. The platform does not impose the schema; the surface and the agent agree on it.

**Rationale.**

- Once everything is a memory entry, tasks don't need a separate API, a separate visibility axis, or a separate lifecycle vocabulary. The memory entry already has all of those.
- Task lifecycle is naturally append-only on `AgentMemory`: each state change is a new entry referencing the prior. The append-only invariant from Q7 carries over.
- Cross-thread task visibility falls out of the same `ThreadMemoryPolicy` / `threadOnly` mechanics as any other memory entry. No special-case rules.
- The UX layer keeps the task concept (panel, `#name`, lifecycle indicators); the platform layer doesn't.

---

## 6. Participant-set changes — state machine

**Decision:** A thread's identity is its permanent membership set. Membership changes within an existing set are **per-(thread, participant) state transitions**, not new threads. Membership-set changes (a new participant joins for the first time) are new threads.

- A thread with members `{H1, A1, A2}` always lists those three as members, regardless of who is currently active. **Threads don't end.** A thread persists across all participant state changes; it is the canonical lifelong record.
- Each `(thread, participant)` pair has a per-thread state machine: `added → active → removed → re-added (active)`. Re-add is allowed and does **not** create a new thread.
- A removed participant **does not receive new messages** while removed. On re-add, they see only messages and other timeline artifacts from periods when they were active. There is a clearly-defined **blackout window** between `removed` and the next `added` event during which the participant sees nothing.
- **Adding a participant who has never been a member of this thread** is a different operation: it produces a *different participant set*, hence a *different thread*. The engagement (UX) may absorb this transition transparently for the user (see below), but the underlying threads are distinct.
- The Timeline event for a state change is **`ParticipantStateChanged(thread, participant, from_state, to_state, timestamp)`**. (Replaces the earlier draft's `ThreadTransitioned` / `ConversationTransitioned`.)

**Engagement absorbs the change.**

- The engagement (UX) is the user-visible relationship surface. It may stitch together state changes within a single thread (membership transitions on the timeline) and, separately, may stitch *across distinct threads* when the user adds a new participant who wasn't in the original membership (creating a new thread).
- Concretely: a user Hm has an engagement view with their writing agent A1. The engagement starts on thread T1 = `{Hm, A1}`. Hm adds a colleague Hm2 to the conversation — this is a new participant set, hence a new thread T2 = `{Hm, A1, Hm2}`. The engagement timeline renders both threads with a "Hm2 joined" system marker, but the underlying threads T1 and T2 are distinct.
- Within a single thread, participant state changes (e.g., A2 told to leave T1, then later re-added to T1) render on T1's timeline as `ParticipantStateChanged` events.

**Rationale.**

- The participant-set invariant is non-negotiable (it is the whole framing). The previous draft tried to honour it by ending thread A and starting thread B on every membership change — but that conflated two genuinely different operations (a state change inside an existing set vs. a change in the set itself), inflated the thread count, and made every leave-then-rejoin produce a new thread record. Folding intra-set transitions into a per-participant state machine on a single thread keeps the lifelong-record promise crisp without violating the invariant.
- Threads don't end is the load-bearing simplification. The thread is the canonical permanent record of "the participants who were ever part of this conversation"; the per-participant state field tells you *when* each was active. Audit, recall, re-add are all simple reads on a stable record.
- The blackout window is a clear, enforceable read-side rule: the platform serves the participant only the timeline slices for periods they were `active`. The agent does not have to hand-filter; the platform filters at the read.
- A removed participant's memory entries scoped to that thread are **not** purged — they are frozen at the moment of removal and resume accumulating when re-added. The blackout-window timeline content is unreachable to that agent on re-add.

**Consequences.**

- D1's contract carries thread membership as the permanent set + a per-(thread, participant) state field. The state machine and its allowed transitions are documented at the contract level.
- The activity-event projection adds `ParticipantStateChanged` events; removes any `ThreadTransitioned` event from the prior draft.
- E2's UX work covers two distinct cases: (a) state changes within a thread (timeline marker, no thread-id change in URL); (b) participant-set changes that create a new thread (engagement timeline stitches multiple thread URLs together transparently).
- The engagement stitching across distinct threads (case b) is a UX projection, not a new persisted entity at the API surface. The engagement is a relationship narrative; the threads it spans are the persisted records.
- **Hosted-service foundation:** the per-(thread, participant) state field is cheap to store and cheap to filter on; no problem.

**Visibility filter — what a participant sees on the Timeline.**

The blackout window is enforced by a precise read-time filter. The Timeline (Q7) doesn't change shape; the filter is computed from data the Timeline already carries — each participant's `ParticipantStateChanged` events plus each artifact's timestamp.

For participant `P` viewing thread `T`'s Timeline, an entry `E` is visible iff **either**:

1. **`E` is a `ParticipantStateChanged` event whose target is `P`.** P always sees their own state transitions — they need to know they were removed and re-added, even when the events flank a blackout window.

   **OR**

2. **`P`'s state at `E.timestamp` is `active`,** where state is determined by:
   - The most-recent `ParticipantStateChanged` event affecting `P` with timestamp **strictly less than** `E.timestamp`, OR
   - The default state `active` if `P` is a member and no state-change event has occurred yet (thread creation puts all members in `active`).

   The strict `<` matters: at `t_leave` (when P transitions `active → removed`), the most-recent affecting event is the leave event itself — but using strict-less-than, P's state at `t_leave` is computed from before the leave (which is `active`). So the leave event itself is captured cleanly by clause 1, and entries strictly after `t_leave` get state `removed`.

Pseudocode:

```
def visible(E, P, T):
  if E.type == "ParticipantStateChanged" and E.target == P:
    return True
  events = [e for e in T.timeline
              if e.type == "ParticipantStateChanged"
              and e.target == P
              and e.timestamp < E.timestamp]
  state = events[-1].new_state if events else "active"   # default for members
  return state == "active"
```

**Two clarifications about what is *not* Timeline-filtered:**

- **Membership set and current per-participant state are live thread properties, not Timeline content.** Any participant of the thread sees the full membership set + every participant's *current* state at all times. (Otherwise re-joining feels broken — "is Q in the room right now?" needs an answer regardless of when P was last active.) Only the *historical Timeline* is filtered.
- **Other participants' state-change *trajectory* during P's blackout is invisible to P.** P sees current state, but not the path through it. Example: while P is removed, Q transitions `removed → active → removed → active`. When P rejoins, P sees Q's current state is `active` but does not see the three intervening transitions.

**Implementation note.** Optimisations (caching P's active intervals per `(P, T)`, binary search at query time) are straightforward and don't touch the model.

**Settled rejection — subset memory access.** During PR review the user explored a "subset memory access" idea: if T2's participant set is a subset of T1's, agents in T2 would get read-side access to their T1-scoped memory entries. **Rejected for v0.1** because it sidesteps `ThreadMemoryPolicy` and re-introduces leakage the policy is meant to prevent. The continuity story for users moving between threads relies on the standard primitives: cross-thread visibility via `threadOnly: false` (when policy allows), or explicit user-driven manual quoting / referencing of prior thread content. This is a settled rejection, not an open question.

---

## 7. Initiative messages and the Timeline

**Decision:** An initiative-driven agent message (task completed; reminder fired; observation digest summary) is a **normal thread message** posted onto the thread's Timeline. It is not a separate primitive at the platform level. The message may carry **optional UX-hint metadata** in the payload's `context` field: `{ kind: "task_update" | "reminder" | "observation" | "spontaneous", task: "#name"?, originating_message: <id>? }`. The metadata is a hint for the engagement / collaboration surface to render a header or grouping cue. **The platform does not branch on `context`.**

### Timeline

Every thread has a single, ordered, timestamped **Timeline** of artifacts. The Timeline is the unifying concept Q5, Q6, Q7, and Q8 reference.

- Artifact types: **Message** (user / agent / initiative), **`ParticipantStateChanged`** (Q6), **Retraction** (Q8), and **system events**.
- Tasks are memory entries (Q5), not Timeline artifacts. The collaboration surface renders task state by reading the agent's task-shaped memory entries, not by walking Timeline artifacts.
- The Timeline is **append-only at the platform level.** Edits and retractions (Q8) appear as new Timeline events that reference the prior artifact, not as in-place mutations.
- **Per-thread FIFO** (Q3) is the ordering invariant on the Timeline.
- Initiative messages, user messages, and agent reply messages are all Messages on the same Timeline — distinguishable only by sender role and by the optional `context` UX hint.
- **The per-participant view of the Timeline is filtered**, not a separate copy. The filter rule (visibility based on the participant's `active` intervals + their own state-change events) is specified in Q6.

**Rationale.**

- Conflating "initiative messages have a separate envelope" with "the platform branches on initiative" was overreach in the prior draft. The platform doesn't need to branch; the UX renders the hint differently. Strip the platform-level distinction and let the surface do the framing.
- The Timeline gives Q6 (participant state), Q7 (initiative), and Q8 (retraction) a single shared concept to reference. State-changes-on-timeline, retractions-on-timeline, initiative-messages-on-timeline are all uniform — different artifact types, same ordering, same persistence rules. (Tasks are memory entries per Q5, not Timeline artifacts.)
- The `context` hint is still useful: a task-update reply shown next to its originating user message reads cleanly when the surface can show a "re: #flaky-test-fix" prefix; reminders / observations want a different visual register than direct replies. But this is the surface's job, not the platform's.
- Append-only at the platform level matches the "lifelong record" promise on the thread: the canonical record of what happened never mutates; corrections (Q8) and retractions are new events that reference the originals.

**Consequences.**

- D1's outbound message contract carries optional `context` metadata; the platform does not enforce or interpret it beyond passing it through to clients.
- F2's `messaging.md` revision documents the **Timeline as a first-class concept** (it currently doesn't have one) — artifact types, ordering, append-only rule.
- The activity-event projection is the implementation of the Timeline; F2 reconciles the naming.
- E2 renders `context` hints on the collaboration surface (e.g. "re: #flaky-test-fix" header on a task-update message). The set of `kind` values is a UX vocabulary for now; if it needs to be a typed enum at the contract level later, F3 / D1 can pin it.

---

## 8. Misinference and correction UX

**Decision:** Correction is **a normal user message** that the agent interprets, not a separate API verb or a special "undo" lifecycle. The platform supplies **one primitive** — `message.retract` — for the agent to mark a previously-sent message as retracted on the thread Timeline; everything else (cancelling work, re-scoping a task) is the agent updating its memory entries via Q4's `store(memory)`. A **collaboration affordance** pre-templates the correction message:

- **`message.retract(message_id, reason)`** — the agent marks one of its own previously-sent messages as retracted. Per the Timeline append-only rule (Q7), the retraction is a **new Timeline event** that references the prior message; the original message remains on the Timeline (audit) and the surface renders it with a strikethrough + the agent's stated reason. Messages are still a typed concept — the thread is a sequence of messages between participants; retraction marks a sent message as retracted on the shared Timeline. This is for "I told the user something wrong; let me say so explicitly" correction.

The **collaboration affordance** gives the user a "this isn't what I meant" one-click action on any message that triggered in-flight work — it prefills a templated message ("re: #flaky-test-fix — I meant the integration test, not the unit test. Please redo with that scope.") and leaves the user free to edit before sending. The agent receives the message normally and interprets it (typically by storing a new memory entry that updates the prior task entry's state).

**Rationale.**

- The platform's job is to model what's structurally distinct. A retraction is structurally distinct because it modifies the thread's shared Timeline rendering — what *participants* see. A task update is *not* structurally distinct from any other memory write; it's an entry in `AgentMemory`, the agent's private state evolution.
- The platform should not invent a "correction" wire format the user (or the agent) has to learn. Corrections are how humans naturally communicate — "wait, I meant…" — and the agent's strength is exactly interpreting that. The platform's job is to give the agent the primitive to fix the *record* (`message.retract`) and to give the user a shortcut for the common case (the collaboration affordance).
- "Discard / redo / re-scope" are no longer platform primitives — the agent interprets the user's correction and stores a new memory entry that updates the task's state. Q4's `store(memory)` is enough.
- The collaboration affordance avoids the user having to remember the right phrasing or the task tag. A click pre-fills it; the user only has to add the corrected content. This is the same pattern as a "reply" button in a chat client.

**Consequences.**

- D1 / the public Web API exposes `message.retract` as the only correction-shaped MCP tool the agent calls. The collaboration affordance is a pure E2 deliverable that calls existing send-message endpoints; no new endpoint required.
- The "retract" semantics are deliberately soft (Timeline event + strikethrough render, not delete). Hard delete is a separate compliance concern (right-to-be-forgotten), out of scope for v0.1.

---

## 9. Cold start

**Decision:** Cold start is **not a platform concept.** There is no `instructions.opening_offer` agent-definition field, no `is_first_contact` SDK hint, no platform-mandated greeting flow. Cold-start behaviour is split between two layers, neither of them platform:

- **Empty-state UX** (empty engagement / collaboration views, idle states, first-impression rendering when nothing has happened yet) is owned by **E2** and the broader engagement / collaboration design. The right shape differs by context — 1:1 vs group, agent vs unit, work-focused vs conversational — and a single platform field would be either too rigid or too vague.
- **Agent-side first-message behaviour** (whether and how an agent introduces itself, suggests what it can do, asks an opening question, adapts to a 1:1 vs multi-party participant set) is the **agent's own runtime configuration** — its prompt, its system message, its agent-runtime-specific instructions. The agent decides based on what it knows about the thread.

For example: an agent built on the Claude CLI can put cold-start instructions in `~/.claude/CLAUDE.md` (which the runtime reads on every session start) — e.g., *"in a new session that represents a thread with a single human, send an intro message."* An agent built on a different runtime uses that runtime's equivalent. The platform does not need to standardise the mechanism — different runtimes have different conventions, and trying to abstract over them would constrain rather than help.

**Rationale.**

- Cold-start behaviour varies by context (group vs 1:1, agent vs unit, work vs conversation, recurring partner vs first-meeting). A platform-level field for it would either be a single shape that doesn't fit half the cases, or a vague string the platform doesn't interpret.
- Agent prompts already shape behaviour. Cold-start behaviour is just behaviour. Adding a dedicated field for it duplicates the agent runtime's prompt mechanism.
- The engagement / collaboration surface (E2) is the right home for empty-state UX — it owns the design context for what reads as "ready" vs "broken."
- Letting agent runtimes handle cold-start configuration their own way is the open-platform-friendly choice. Different runtimes (Claude CLI, OpenAI Assistants, custom) have different conventions; standardising forces a least-common-denominator.

**Consequences.**

- D1 drops `is_first_contact` from the `on_message` SDK contract (proposed in a prior F1 draft; never implemented).
- D1 drops the `instructions.opening_offer` agent-definition field (proposed in a prior F1 draft; never implemented).
- E2 owns empty-state UX: empty-engagement view, empty-task-panel state, first-message rendering when an engagement opens, idle-state affordances.
- Agent authors handle cold-start behaviour through their agent runtime's prompt mechanism. Documentation on how to write good cold-start prompts for common runtimes (e.g., `~/.claude/CLAUDE.md` patterns) belongs in the agent-author guide, not the platform spec.

---

## 10. Migration

**Decision:** **v0.1 is not migrated to.** There is no live deployment to preserve; pre-OSS internal usage produced no production data the platform owes migration to. The platform is free to change schemas, API surfaces, wire formats, MCP tool names, and identifier shapes for v0.1 without backwards-compat constraints — no legacy partition, no `/conversations` URL alias, no field-name aliasing.

**Forward-looking migration strategy.** Future schema / API changes (v0.1 → v0.2 → ...) will be migrated through the standard versioning + deprecation policy on the public Web API per `docs/architecture/web-api.md`, not through partition-of-old-data tactics. v0.1 establishes the post-rename shape; subsequent releases evolve from there.

**Rationale.**

- There is no live OSS deployment of Spring Voyage prior to v0.1. (See `docs/plan/v0.1/decisions.md` for the release history this rests on.)
- Internal-only usage data is the developers' problem, not the platform's. Developers can re-create their state under the new model; no automation is needed.
- The legacy-partition design from the prior draft was solving a problem that doesn't exist. Dropping it removes a real complexity tax — alias routing, dual schema reads, deprecation timer, "is this thread legacy?" checks — for no benefit.
- Reserving back-compat for the standard Web API versioning policy keeps the migration story uniform across releases, rather than baking a one-off legacy-partition mechanism that exists solely for a v0.1 reset.

**Consequences.**

- The `/conversations/{id}` URL is **not preserved.** C2 introduces `/threads/{id}` as the canonical URL; no alias.
- Pre-v0.1 data is the developers' concern; if they want to re-enter it, they re-enter it under the new model.
- The on-disk field name `conversationId` does not survive — there is no legacy partition to host it. The code-level rename `ConversationId` → `ThreadId` is full and uniform.
- Forward migrations (v0.1 → v0.2, etc.) follow the public Web API versioning + deprecation policy.
- **Web Portal continuity:** the portal renders against `/api/v1/threads/{id}` directly; no "legacy archive" banner is needed because there is no legacy archive.
- **Hosted-service foundation:** clean schema from day one means the hosted operator does not inherit a dual-schema burden.

---

## Downstream artefacts

This design unblocks the following work, none of which is in scope for the F1 PR:

- **F2 — doc revisions.** Sweep `Conversation*` → `Thread*` terminology across `docs/architecture/messaging.md`, `docs/architecture/units.md`, `docs/architecture/agent-runtime.md`, and the glossary; the canonical glossary entries for **Thread**, **Engagement**, and **Collaboration** ship in this PR alongside this doc to anchor F2. Introduce the **Timeline** as a first-class concept in `messaging.md` — and reflect that **tasks are not Timeline artifacts** (Q5): the artifact-types list is **Message**, **`ParticipantStateChanged`**, **Retraction**, and **system events**; task lifecycle is memory-entry-shaped (`store(memory)` + recall), not API-primitive-shaped. Sweep `MemoryFlowPolicy` (named in the prior F1 draft, never landed in code) and the now-retired `MemoryPromotionPolicy` (also a prior-draft term) → **`ThreadMemoryPolicy`**, and replace the two-store framing in `messaging.md` / `units.md` with the single-store `AgentMemory` + `MemoryEntry` view per Q4. Same pass updates the docs to reflect the participant-set model, the Engagement / Collaboration UX surfaces, the per-thread FIFO contract, the optional `context` UX-hint metadata on messages (Q7), the `ParticipantStateChanged` Timeline event and per-(thread, participant) state machine (Q6), and the renamed MCP tools (`store` / `recall`, Q4). Mark ADR-0018 as **Superseded by** F3's new ADR in the same PR.
- **F3 — new ADR.** Author the participant-set + state machine + memory model ADR using the new terminology throughout. Cites this doc, ADR-0026 (per-agent container), and ADR-0029 (boundary), and supersedes ADR-0018. Anchors the long-form rationale; this doc anchors the per-question decisions.
- **D1 — wire contract.** Specify: `thread_id` field on inbound messages (Q3); optional `context` metadata on outbound messages (Q7); per-(thread, participant) state field (Q6); `concurrent_threads: bool` agent / unit definition flag (Q3); the `MemoryEntry` shape and the `ThreadMemoryPolicy` attached to threads (Q4). Surface `store`, `recall`, `message.retract`, `peek_pending` as MCP tools. (No `task.*` tools — tasks are memory entries per Q5. No cold-start fields — Q9 makes that a UX + agent-runtime concern.)
- **C2 — public Web API target shape.** Introduce `/api/v1/threads/{id}` as the canonical URL surface. **No `/conversations` alias and no backwards-compat needed** (Q10). The Engagement-as-stitching-projection surface (Q6) sits on top — engagements are a UX projection, not a separately persisted entity at the API.
- **E2 — engagement and collaboration UX.** The user-facing surfaces are explicitly **Engagement** (the navigation list / relationship view, Q6) and **Collaboration** (the active workspace where the user works, Q7–Q8), not "dialog." Build: the engagement view that absorbs participant-set transitions across distinct threads (Q6); per-(thread, participant) state markers on the Timeline within a thread (Q6); the "this isn't what I meant" collaboration affordance (Q8); the `#name` reference inline rendering and `context` UX-hint header rendering (Q7); empty-state UX for both engagement and collaboration views (Q9 — empty engagement lists, empty task panels, first-impression rendering when nothing has happened yet). The collaboration surface owns the **task-panel rendering convention** (Q5) — interpreting task-shaped memory entries from the agent's `AgentMemory` and rendering lifecycle; the platform does not impose the schema, so E2 and the agent agree on what a task-shaped payload looks like.
- **Code-level rename — Area G.** `IConversationQueryService` → `IThreadQueryService`, `ConversationId` → `ThreadId`, `Message.ConversationId` → `Message.ThreadId`, the activity-event projection's `CorrelationId == ConversationId` mapping, and every repository / actor / test that references `Conversation*`. **Not in scope for F1.** Tracked in the follow-ups list below (#1287).
- **Deferred memory features — single follow-up.** Unit recursion; cross-thread reads at the recall API level; multi-human permission gating on memory access; inferences with explicit provenance (thread-less entries linked to source entries). Not part of v0.1; warrant design when there's a forcing case (Q4 deferral note).
- **F execution plan.** The execution-plan issue (deliberately deferred per the plan-of-record) opens once F3's ADR lands.

---

## Follow-ups to file

The following adjacent items surfaced during this design but are out of scope for F1 / F2 / F3 / D1 / C2 / E2 above. Listing here for the orchestrator to triage; do not widen any current PR for them.

**Folded into the design (no longer follow-ups):**

- *Per-thread privacy / reuse policy.* The prior draft flagged this as a separate follow-up; it is now `ThreadMemoryPolicy` (Q4).
- *Web Portal URL backwards-compat for `/conversations/{id}`.* Q10 reverses to "no migration"; there is no legacy URL to preserve.
- *Operator tooling for the legacy archival sweep.* Q10 reverses; there is no legacy partition.
- *MCP tool rename migration (`storeLearning` / `recallMemory` → `store` / `recall`).* Q10 reverses; there is no deprecation window to manage.
- *Standing-offer authoring guidance.* Q9 drops `instructions.opening_offer` as a platform field; cold-start behaviour is the agent runtime's prompt mechanism, not a platform schema. Agent-author guidance for cold-start prompts is now ordinary agent-author documentation (Area B), not a platform-spec follow-up.

**Still open:**

1. **Code-level rename `Conversation*` → `Thread*` types and properties.** `IConversationQueryService`, `ConversationId`, `Message.ConversationId`, the activity-event projection's `CorrelationId == ConversationId` mapping, and every repository / actor / test that references `Conversation*`. Area G scope; sequence with the F2 doc rename. Already filed as #1287.
2. **CLI verb rename `spring conversation …` → `spring thread …`.** Area E1 scope; ship once the code rename (#1287) settles to avoid a half-renamed surface. Already filed as #1288.
3. **New long-form positioning doc `docs/concepts/threads.md`.** Captures the full UX positioning writeup — Engagement vs. Collaboration, example copy, when to use which term, the user-facing narrative. Area B (docs overhaul) scope. F1 / F2 / F3 ship the architecture and the glossary anchors; the long-form positioning is a separate piece of writing. Already filed as #1289.
4. **Hard-delete / right-to-be-forgotten on retracted messages and removed-participant blackout-window content.** Q8 leaves retract as soft (Timeline event + strikethrough render); Q6 leaves a removed participant's thread-scoped memory entries frozen rather than purged, and the blackout-window Timeline content unreachable but undeleted. Compliance regimes (GDPR, CCPA) require hard delete on user request. Out of scope for v0.1; needs an architectural decision on how erasure interacts with the append-only Timeline.
5. **`#name` namespace collision rules in multi-participant threads.** Per Q5, `#name` is a UX / surface convention for addressing a task-shaped memory entry in the current thread — it is not a platform-typed thing. The namespace-collision question (two participants in a thread both reference `#flaky-test-fix`) remains a UX-rules item over that surface convention. E2 should file the UX rule before building the collaboration surface.
6. **v0.1 deferred memory features (cross-thread / multi-human).** Unit recursion (a unit reading its members' thread-scoped memory entries from threads the unit itself is not in); cross-thread reads at the recall API level by any agent or unit; multi-human permission gating on memory access. The simple v0.1 model (Q4) does not need any of these; design when a forcing case appears. Already filed as **#1292**.
7. **v0.1 deferred memory features (inferences with explicit provenance).** Agent-driven entries that synthesise across multiple sources, with relationship-style metadata linking inferred entries to their source entries — likely as first-class memory relationships forming a hypergraph (relationship type as a label, sets of source / destination entries, directed traversal, depth control). Out of scope for v0.1; design when a forcing case appears. Already filed as **#1293**.
