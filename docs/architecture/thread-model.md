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

Memory has two layers: **per-thread** ("what we've done together") and **agent-level spanning** ("what I've learned across everyone"). Cross-thread flow between the two is policy-governed. Rationale and the broader vision live in #1123 — this document does not restate it.

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
- The current public Web API exposes `/api/v1/conversations`. The new canonical surface is `/api/v1/threads`; `/conversations` may stay as a backwards-compatibility alias. C2 owns the URL story and the deprecation window; flagging the implication here, not deciding it.
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

**Rationale.**

- Auto-batching sounds like a free latency win but it shapes agent reasoning in a way the platform should not impose. An agent that is mid-tool-call for message N may want to finish before reading N+1 (correctness); a chat-shaped agent may want to immediately collapse N+1 into the working context (latency); an LLM doing structured planning may want to read all pending messages once and re-plan (token efficiency). The right answer differs per agent. The platform should not pick.
- One-per-invocation matches the A2A wire model in ADR-0029 (one A2A request per inbound message; the agent's response may stream). Auto-batching would either invent a non-A2A bulk message shape or fold N user turns into one A2A request, both of which fight the protocol.
- An agent peeking the queue at a checkpoint is exactly the existing `checkMessages` semantics from ADR-0018 / `messaging.md`. Lifting it to a first-class SDK accessor formalises a pattern delegated agents already use; it is a small surface, not a new mechanism.
- Ordering is preserved: per-thread messages dequeue in arrival order, FIFO. The platform does not promise causal ordering across threads (different participant sets can race) but does promise per-thread FIFO, which is the only ordering an agent reasoning about "what just happened in this thread" can sensibly use.
- Suspension semantics from ADR-0018 (an agent can suspend the active thread, run another, resume) carry forward unchanged. Suspension is at thread-grain, which is the right grain for "I am blocked on user input here, let me work on something else."

**Consequences.**

- D1's `on_message` contract carries a thread-identifying field, `message_id`, and a `pending_count` hint (cheap signal that more messages are queued — agent decides what to do with it). The on-the-wire field name (currently `conversation_id`) should be renamed to `thread_id` to align with the new terminology; D1 owns the call on exact field shape and migration window.
- The control-channel / observation-channel model from ADR-0018 stays as-is: control still pre-empts thread work; observations still arrive as a batched digest. (These channel names refer to mailbox channels, not to the Thread / Engagement / Collaboration trio; they are unaffected by Q1.) The Q3 decision is about per-thread message-channel shape only.
- F2 updates `messaging.md` to clarify that "active thread" semantics are unchanged but that the per-thread FIFO contract is now a documented promise (not just an emergent property of the actor turn model).

---

## 4. Cross-thread memory flow defaults

**Decision:** **Default is asymmetric and conservative.**

- **Up (per-thread → spanning):** the platform extracts and lifts **only durable identity facts** that the agent itself flags via a `learn(...)` SDK call (replaces today's `storeLearning` MCP tool). The platform never introspects message content to auto-promote. Spontaneous learnings don't escape a thread unless the agent declares them.
- **Down (spanning → per-thread):** the agent's spanning memory is **always available for retrieval** (via a `recall(...)` SDK call mirroring `recallMemory`) but is **never auto-injected** into a thread's prompt context. The agent decides per turn whether to pull spanning memory in.
- **Extensibility seam:** a tenant-, unit-, or agent-scoped **`MemoryFlowPolicy`** can override defaults along three axes: `auto_promote` (what classes of fact may be promoted without the agent flagging — default `none`), `auto_recall` (whether to pre-warm spanning memory into Layer-2/3 prompt context — default `off`), and `cross_thread_visibility` (per-thread memory readable from sibling threads — default `false`). The `MemoryFlowPolicy` name stays as-is; "Memory" in the name refers to the memory subsystem, not to the Thread / Engagement / Collaboration trio.

**Rationale.**

- "Default conservative" is the only safe default for a multi-tenant platform. The cost of a leak (information from thread A surfacing in thread B without the user's expectation) is much higher than the cost of an agent occasionally having to call `recall()` to find a fact it could have remembered automatically. Latency is recoverable; trust loss is not.
- Asymmetric default (up needs an explicit flag; down is read-on-demand) maps to how existing CLI tools (Claude Code, Codex) treat their own state: the agent commits to memory deliberately; reads from it freely.
- A policy seam is necessary because consumer products and enterprise deployments will want different defaults. A consumer agent for a personal user wants aggressive auto-recall; an enterprise compliance-bound deployment wants strict isolation. We do not pick one — we ship the safe default and the lever.
- Layering matches ADR-0029's "memory deferred" hold: memory is **not** at the SDK boundary. `learn` / `recall` are MCP tools the platform offers via the public Web API (per ADR-0029's "capabilities reach via MCP" pattern), not bucket-1 SDK hooks. Agents invoke them; the platform stores and policy-gates them.

**Consequences.**

- F3's ADR ratifies the two-layer memory model and the default policy. The actual storage shape (per-agent volume? per-tenant store? both?) is left to D1's memory contract under ADR-0029 Stage 4 — F1 does not pre-empt it.
- The existing `storeLearning` / `recallMemory` MCP tools are renamed and re-spec'd as `learn` / `recall` in the public-API surface. The semantic shift is small but real (the platform now distinguishes per-thread from spanning storage). C1 / C2 own the Web API surface change.
- The `MemoryFlowPolicy` resolution chain (agent → unit → tenant) follows the same precedence rules as cloning policy (ADR-0029 + the agent-cloning-policy precedent in `units.md`): tightest non-null value wins; allow-lists intersect.
- **Per-thread privacy / reuse policy** — flagged but deliberately not specified here. The user has indicated a thread-level toggle controlling whether participants may reuse information captured or inferred inside a thread beyond the thread's boundaries is a design question to revisit. F1 does not pre-empt that policy; see the follow-ups list at the end of this doc.
- **Hosted-service foundation:** the conservative default and the policy lever are exactly what a hosted service needs — safe by default, configurable for paying tiers.

---

## 5. Cross-thread task visibility default

**Decision:** Tasks are **agent-level** entities by default — a task lives in the agent's task store, not in any one thread. **The default visibility is: a task is referenceable from any thread in which the agent participates with the same primary user**, and is **not** referenceable from a thread that does not include the user who originated it. The override surface is the same `MemoryFlowPolicy` from Q4, with a `task_visibility` axis: `originator-only` (default), `thread-only` (one specific thread; the legacy ADR-0018-shaped read), or `agent-wide` (every thread can see every task).

**Rationale.**

- The reframing in #1123 explicitly decouples tasks from threads: tasks are referenced by `#name`, have lifecycles driven by actual work, and outlive any one message exchange. A task is something the agent is doing; the thread is the channel through which the work was requested.
- "Visible only to participant set A" (the conservative read of the invariant) breaks a real and common case: I ask my coding agent to "fix that flaky test #flaky-test-fix" in our 1:1 engagement. I then add my colleague to it — that's a different participant set, hence a different thread. The task should still be there. Forcing the user to re-introduce the task in the new thread is bad UX and breaks the task-panel framing.
- "Visible across every thread including those with other users" is too leaky — it means a task I asked my coding agent to do at home shows up when my agent is working with my employer in a different engagement. That violates the trust boundary the participant set is supposed to express.
- The originator-anchored default ("all engagements that include the originating user") threads the needle: tasks follow the human who asked for them, regardless of which other peers are in the room. A user-private agent (no other participants) sees only that user's tasks. A user adding a colleague to an engagement brings their own tasks; the colleague's tasks from a different engagement do not appear.
- The default can be overridden in either direction by `MemoryFlowPolicy`. Enterprises that want strict per-thread isolation set `thread-only`; agents whose tasks are inherently shared (a team unit's task board) can set `agent-wide`.

**Consequences.**

- The `IAgentActor` task store becomes a first-class concept, not a derived projection over thread events. F3's ADR captures this. F2 updates `messaging.md` to note that tasks are now agent-scoped, not thread-scoped.
- The task `#name` reference resolves through the task store with the originating user as the lookup key; multi-user threads require disambiguation only when two users share a task name (`#flaky-test-fix` from user A vs. from user B). UX rules for disambiguation are E2's call.
- Task initiation telemetry needs to record the originating user explicitly; without it the visibility check is impossible. D1 adds `originator_user` to the task initiation contract.

---

## 6. Participant-set changes UX

**Decision:** The user sees **one continuous engagement with the agent**; under the hood the platform records a **transition** from thread A (participant set X) to thread B (participant set Y) and renders both in the same engagement timeline with a visible **system message marker** ("Alex joined this engagement" / "Sam left this engagement"). Both threads remain queryable by id; the engagement stitches them. Specifically:

- **Adding a participant** ends the active thread A and begins a new thread B. The old A's per-thread memory is **referenceable but not auto-injected** into B's context. The agent (or the user) can pull A's history into B explicitly via the same `recall()` mechanism from Q4. The engagement timeline shows both, separated by the join marker. Tasks initiated in A remain visible in B per Q5.
- **Removing a participant** ends the active thread A and begins a new thread B with the smaller participant set. A is **archived**, not deleted; the engagement timeline shows the leave marker and continues with B.
- **No "branching" semantics** — the platform never holds two live threads with overlapping participant subsets in the same engagement. Removing-then-re-adding the same participant produces a new thread each time (two transitions); the engagement stitches all three.

**Rationale.**

- The participant-set invariant is non-negotiable (it is the whole framing). The user-visible surface has to absorb the fact that the kernel's identity changes; "one continuous engagement" is the absorption.
- "Continuity of context vs. clean break" is a false binary. The kernel does a clean break (new thread, separate per-thread memory); the surface shows continuity (one engagement, transition markers, opt-in pull of old history). This matches how iMessage / Slack handle group changes — the surface continues; system messages mark the membership change; the underlying topic key changes silently.
- Auto-injecting A's full context into B is the wrong default for the same reason as Q4: the new participant did not consent to receiving A's history just by joining the engagement. Pulling history is an explicit act, either by the user ("show me what we discussed before Alex joined") or by the agent ("I will remind everyone of the prior context").
- Archiving (vs. deleting) on removal is necessary for audit, recall, and the not-uncommon case of re-adding the same participant — even though re-add is a new thread per the invariant, the prior history should still be referenceable.
- This is the **single piece of UX behaviour where the kernel invariant most visibly strains the user model.** Flagging it: the strain is intentional and well-bounded — the engagement absorbs it cleanly — but E2's UX work has to prototype the transition markers and the recall-prior-context affordance carefully. If user testing later shows the markers are confusing, the fallback is "implicit auto-recall of the previous thread's last N messages into B," which is still consistent with the invariant.

**Consequences.**

- Engagement is a **first-class persisted concept** at the API surface, not just a UX projection — it stores the ordered list of thread ids it stitches, plus participant-change markers. C2 / D1 own the contract.
- The activity-event projection (`messaging.md` § Thread Surfaces, post-F2-rename) gets a new event type: `ThreadTransitioned(from, to, reason: "participant_added"|"participant_removed", participant)`. F2 documents this.
- **Web Portal continuity:** the portal currently treats `conversation` as the user-visible surface (`/conversations/<id>`). The portal must keep working. F2 / C2 call: the existing `/conversations/<id>` route in the portal becomes an engagement view that stitches multiple underlying threads transparently for the user; whether the canonical URL flips to `/engagements/<id>` (or `/threads/<id>` for the system-level view) is C2's call, with backwards-compat on the legacy URL preserved.
- **Hosted-service foundation:** the engagement projection is cheap to render and cheap to host; no problem.

---

## 7. Initiative messages

**Decision:** An initiative-driven agent message (task completed; reminder fired; observation digest summary) is sent as a normal message into the appropriate engagement and carries a **`context` envelope field** identifying what triggered it. Specifically: `context = { kind: "task_update" | "reminder" | "observation" | "spontaneous", task: "#name"?, originating_message: <id>?, summary: <short text> }`. The collaboration surface renders the context as a small inline header on the message ("re: #flaky-test-fix" / "scheduled reminder" / "while you were away"), giving the user a click target back to the relevant task or to the originating message.

**Rationale.**

- Without a sub-thread / sub-conversation concept, the agent cannot say "in our previous chat we discussed X." It has to say "here is a message" and tag the message with metadata that lets the UI render an unambiguous "what is this about" hint. The metadata is the substitute for in-engagement navigation.
- Anchoring on `#task` references is the right primary handle: tasks are the user-visible work units (#1123, Q5 above) and the collaboration already supports `#name` as a referenceable token. A click on "re: #flaky-test-fix" opens the task pane.
- For initiative messages that don't have a task anchor (a reminder; an observation digest), the `context.kind` is enough on its own. The UI renders a generic prefix; the user knows the agent is volunteering, not responding.
- Putting the context in the **envelope** rather than encoding it in the message text means the user's reading flow is not interrupted by metadata-as-prose, and the agent's prose can stay clean. The platform is what makes the surface legible; the agent does not have to learn a markup convention.
- This is the same principle as the OS-level "notification" model in mobile platforms: the system frames the notification (icon + app name + grouping) so the app's content can be the actual content.

**Consequences.**

- D1's outbound message contract carries an optional `context` envelope field as defined above. The exact JSON shape is D1's call; this design pins only the semantics.
- F2 updates `messaging.md` to document the `context` field on the message envelope and the activity-event projection consequence: `ContextualMessageReceived` events surface the `context.kind` so observers (other agents, dashboards) can filter on it.
- Hosted-service foundation: notification grouping and unread-state semantics on the engagement surface depend on `context.kind` being a small enumerable set. Platform-imposed enum, not free-form string. F3's ADR pins the enum.

---

## 8. Misinference and correction UX

**Decision:** Correction is **a normal user message** that the agent interprets, not a separate API verb or a special "undo" lifecycle. The platform supplies three primitives the agent / SDK can use to **act** on the correction once interpreted, plus a UX affordance in the collaboration that pre-templates the correction message:

1. **`task.cancel(#name, reason)`** — the agent abandons in-flight work for a task. Stops execution; emits a `TaskCancelled` event; preserves audit trail. Same semantics as ADR-0018's existing cancel — surfaced through the task primitive rather than through a control-channel message.
2. **`task.revise(#name, new_scope)`** — the agent re-scopes a task (replace its goal / instructions) and either restarts it or continues from the current checkpoint. The agent decides which.
3. **`message.retract(message_id, reason)`** — the agent marks one of its own previously-sent messages as retracted. The message stays in the engagement timeline (audit) but renders with a strikethrough + the agent's stated reason. This is for "I told the user something wrong; let me say so explicitly" correction, distinct from task-level correction.

The collaboration gives the user a **"this isn't what I meant" affordance** on any message that triggered an in-flight task — a one-click action that prefills a templated message ("re: #flaky-test-fix — I meant the integration test, not the unit test. Please redo with that scope.") and leaves the user free to edit before sending. The agent receives the message normally and interprets it (typically calling `task.revise`).

**Rationale.**

- The platform should not invent a "correction" wire format the user (or the agent) has to learn. Corrections are how humans naturally communicate — "wait, I meant…" — and the agent's strength is exactly interpreting that. The platform's job is to give the agent the **primitives** to act on the interpretation cleanly (cancel, revise, retract) and to give the user a **shortcut** for the common case (the affordance).
- Distinguishing `task.cancel` / `task.revise` from `message.retract` matters. The first two affect work; the third affects record. An agent that confidently told the user "the test is fixed" and was wrong should be able to retract that statement explicitly (otherwise the engagement history is misleading), even if there is no task to cancel.
- "Discard / redo / re-scope" maps cleanly: `discard = task.cancel`; `redo = task.revise(same scope)`; `re-scope = task.revise(new scope)`. All three are the same primitive.
- The affordance avoids the user having to remember the right phrasing or the task tag. A click pre-fills it; the user only has to add the corrected content. This is the same pattern as a "reply" button in a chat client.

**Consequences.**

- D1 / the public Web API exposes `task.cancel`, `task.revise`, `message.retract` as MCP tools the agent calls. The collaboration affordance is a pure E2 deliverable that calls existing send-message endpoints; no new endpoint required.
- The `TaskRevised` event needs to thread the prior scope and the new scope so observers can audit. F2 includes it in the activity-event taxonomy.
- The "retract" semantics are deliberately soft (strikethrough + reason, not delete). Hard delete is a separate compliance concern (right-to-be-forgotten), out of scope for v0.1.

---

## 9. Cold start

**Decision:**

- **Empty collaboration's task panel, no tasks yet:** show a short, agent-authored **standing offer** rather than empty state. The text is the agent's `instructions.opening_offer` field (a new optional field on the agent definition) or, absent that, a platform-default templated from the agent's role and capabilities ("I can help with: backend code, debugging, code review. Mention #task to start one."). Empty state is a hint, not an error.
- **Engagement on first contact with a new participant set:** show a **single agent-authored greeting message** as the first message rendered inside the new collaboration that opens, computed by the agent on the first turn (the `on_message` for the user's first message returns the greeting + the actual response, optionally as a streamed pair). For purely outbound first contact (initiative-driven; the agent reaches out first to a new user), the first message itself is the greeting + offer; same shape.

**Rationale.**

- Empty states are the place users decide whether the agent is worth talking to. A blank panel reads as "broken." A standing offer reads as "ready." The cost of authoring the offer is one optional config field; the agent owner writes it once.
- Authoring it on the **agent**, not on the platform, keeps the voice consistent (a stoic agent doesn't get a chirpy default greeting; a customer-service agent does). The platform-default is a fallback for agents whose author did not specify one.
- Computing the first-contact greeting on the **agent**, not the platform, again keeps voice consistent and lets the agent adapt to the user (e.g. "I see you have access to the engineering-team unit; want me to start there?"). Pre-rendering it on agent creation is wrong: the platform doesn't know the user yet.
- Platform-default templating (role + capabilities) keeps the experience coherent for agents whose author skipped the offer field — and keeps the agent-author surface optional.

**Consequences.**

- Agent definition gains an optional `instructions.opening_offer: string` field. The field name is intentionally not coupled to the Thread / Engagement / Collaboration trio — it's an agent-author-facing config field. F3's ADR notes it; the actual schema change is D1's. The CLI / portal create-agent flows surface the field; absent value falls back to the platform default.
- D1's `on_message` SDK contract gets an optional `is_first_contact: bool` hint passed to the agent on the first message of a new engagement. Agents can ignore it; SDK defaults wire it into a "preface with greeting" template.
- "First contact" is **per-engagement, not per-thread.** A participant-set transition inside the same engagement (Q6) creates a new thread B but is **not** treated as first-contact for greeting purposes — the user already has prior history with the agent in the same engagement. The engagement is the source of truth for "first contact"; B inheriting from A inside the same engagement means no greeting.

---

## 10. Migration

**Decision:** **Clean-slate-on-upgrade for v0.1.** Existing `conversationId`-keyed data is **archived in place (read-only)** under the existing schema; the new participant-set thread model starts from an empty state on the day v0.1 ships. No deterministic merge, no user-mediated collapse. Pre-v0.1 threads (the data being archived under the new framing) remain queryable through the existing `/conversations/{id}` API for audit and historical reference, but no new messages can be appended to them; new traffic creates new participant-set-conformant threads. A **scheduled archival sweep** (operator-initiated, not automatic) can move pre-v0.1 thread data to cold storage at the operator's discretion; v0.1 ships with the read-only freeze, not the sweep.

**Rationale.**

- v0.1 is the first **public** release; all prior usage was internal-only or pre-public. There is no production data we owe migration to in the sense a public-release-N→N+1 migration would imply. The "existing data" is internal usage data — important for the developers who generated it, not for end users. (See `docs/plan/v0.1/decisions.md` for the release history this rests on.)
- Deterministic collapse (group existing records by computed participant set; merge messages in timestamp order into one new thread) sounds elegant but is wrong in practice: the existing data was authored under "many conversations per participant set" semantics, so the records were intentionally separate **work episodes**, not artificial fragments of one underlying thread. Merging them collapses meaningful structure (different work, different time periods) into one undifferentiated stream.
- User-mediated collapse (prompt the user to choose which records belong together) does not scale and asks users to reason about a kernel concept they did not opt into.
- Clean-slate respects the invariant absolutely from day one. The kernel never has to handle pre-invariant data inline; the new model is uniform. Pre-v0.1 data stays addressable via the same `/conversations/{id}` route (Web Portal continuity preserved for archival reading) but is moved to a clearly-labeled "legacy" partition.
- #1123 anticipated a reset on the next release boundary; v0.1 is that boundary. This is the call.

**Consequences.**

- F3's ADR documents the freeze + archival semantics. F2 updates `messaging.md` and the related projection docs (post-rename) to flag the legacy partition.
- The release notes for v0.1 must call out the data-handling change explicitly: pre-v0.1 records remain read-only; new traffic generates new threads under the new model. This is a release-engineering deliverable, not an F1 design item.
- The on-disk field name `conversationId` survives in the legacy partition because the data was written under that name. A code-level rename to `ThreadId` (Area G / D scope; flagged in the follow-ups list) does not retroactively rewrite the legacy column; the legacy partition keeps its original schema.
- The `/conversations/{id}` URL for archival reading **remains frozen for backwards compatibility.** Whether the canonical surface for new threads becomes `/threads/{id}` (with `/conversations/{id}` aliased for compat) is C2's call; flagging here, not deciding.
- **Web Portal continuity:** the portal must show legacy threads as read-only (no compose box, "this is a legacy archive" banner). E2 / portal team owns the affordance.
- **Hosted-service foundation:** the legacy partition is operator-deletable on schedule. Hosted operators can run a retention policy on the legacy data; OSS operators can leave it indefinitely.

---

## Downstream artefacts

This design unblocks the following work, none of which is in scope for the F1 PR:

- **F2 — doc revisions.** Sweep `Conversation*` → `Thread*` terminology across `docs/architecture/messaging.md`, `docs/architecture/units.md`, `docs/architecture/agent-runtime.md`, and the glossary; the canonical glossary entries for **Thread**, **Engagement**, and **Collaboration** ship in this PR alongside this doc to anchor F2. Same pass updates the docs to reflect the participant-set model, the Engagement / Collaboration UX surfaces, the per-thread FIFO contract, the message `context` envelope (Q7), the `ThreadTransitioned` event (Q6), the agent-scoped task store (Q5), the legacy data partition (Q10), and the rename of MCP tools (`learn` / `recall`, Q4). Mark ADR-0018 as **Superseded by** F3's new ADR in the same PR.
- **F3 — new ADR.** Author the participant-set + Engagement / Collaboration UX ADR using the new terminology throughout. Cites ADR-0029 (boundary), ADR-0026 (per-agent container), ADR-0018 (superseded), and this design doc. Anchors the long-form rationale; this doc anchors the per-question decisions.
- **D1 — `on_message` contract.** Specify the thread-metadata field shape on inbound messages (Q3) — the `conversation_id` field on the wire **should be renamed to `thread_id`**, with D1 owning the call on the exact field shape and migration window — plus the `context` envelope on outbound messages (Q7); add the `is_first_contact` hint (Q9) and the `originator_user` task field (Q5). Surface `task.cancel`, `task.revise`, `message.retract`, `learn`, `recall`, `peek_pending` as MCP tools. **MCP tool names themselves are unchanged in surface name** — the rename is at the field level, not the tool level.
- **Memory contract (ADR-0029 Stage 4).** Pick up the two-layer memory model (per-thread + spanning), the conservative default policy (Q4), and the `MemoryFlowPolicy` precedence chain (Q4 + Q5).
- **C2 — public Web API target shape.** The `/conversations` vs `/threads` URL story is now explicit: the new canonical surface is `/threads`; `/conversations` may stay as a backwards-compat alias for existing portal links and the legacy partition (Q10). C2 owns the deprecation window. The Engagement-as-first-class-resource surface (Q6) sits on top.
- **E2 — new unit/agent UX.** The user-facing surfaces are explicitly **Engagement** (the navigation list / relationship view, Q6) and **Collaboration** (the active workspace where the user works, Q7-Q9), not "dialog." Build the engagement view with participant-change markers (Q6), the cold-start standing-offer + greeting affordances inside the collaboration (Q9), the "this isn't what I meant" affordance in the collaboration (Q8), the `#task` reference inline rendering (Q7), and the legacy-archive read-only banner (Q10).
- **Code-level rename — Area G / D scope.** `IConversationQueryService` → `IThreadQueryService`, `ConversationId` → `ThreadId`, `Message.ConversationId` → `Message.ThreadId`, the activity-event projection's `CorrelationId == ConversationId` mapping, and every repository / actor / test that references `Conversation*`. **Not in scope for F1.** Tracked in the follow-ups list below.
- **F execution plan.** The execution-plan issue (deliberately deferred per the plan-of-record) opens once F3's ADR lands.

---

## Follow-ups to file

The following adjacent items surfaced during this design but are out of scope for F1 / F2 / F3 / D1 / C2 / E2 above. Listing here for the orchestrator to triage; do not widen any current PR for them.

1. **Hard-delete / right-to-be-forgotten on retracted messages and archived threads.** Q8 leaves retract as soft (strikethrough + reason); Q10 leaves legacy data as read-only-archive. Compliance regimes (GDPR, CCPA) require hard delete on user request. Out of scope for v0.1; needs an architectural decision on how erasure interacts with the activity-event projection (the projection is append-only today).
2. **`#task` namespace collision rules in multi-user collaborations.** Q5 notes that two users in the same collaboration may both have a task named `#flaky-test-fix`; disambiguation is "E2's call." E2 should explicitly file the UX rule before building the collaboration surface.
3. **MCP tool rename migration (`storeLearning` / `recallMemory` → `learn` / `recall`).** Existing agent containers calling the old tool names will break when the rename ships. Need a deprecation window + alias on the public Web API. Probably C2's call but worth confirming.
4. **Operator tooling for the legacy-thread archival sweep (Q10).** v0.1 ships with the read-only freeze only. The sweep verb (move-to-cold-storage / purge) is a follow-up release.
5. **Standing-offer template authoring (Q9).** Agents can specify `instructions.opening_offer` but agent-author guidance on what makes a good offer is documentation work, not platform work. Worth filing for the docs overhaul (Area B) once F3 lands.
6. **`MemoryFlowPolicy` precedence + UI for operators.** Q4 + Q5 introduce a new policy type. Needs the same operator-surface treatment as `AgentCloningPolicy` (HTTP, CLI, portal). File once D1's memory contract settles under ADR-0029 Stage 4.
7. **Code-level rename `Conversation*` → `Thread*` types and properties.** `IConversationQueryService`, `ConversationId`, `Message.ConversationId`, the activity-event projection's `CorrelationId == ConversationId` mapping, and every repository / actor / test that references `Conversation*`. Area G scope; sequence with the F2 doc rename.
8. **CLI verb rename `spring conversation …` → `spring thread …`.** Area E1 scope; ship once the code rename (#7) settles to avoid a half-renamed surface.
9. **New long-form positioning doc `docs/concepts/threads.md`.** Captures the full UX positioning writeup — Engagement vs. Collaboration, example copy, when to use which term, the user-facing narrative. Area B (docs overhaul) scope. F1 / F2 / F3 ship the architecture and the glossary anchors; the long-form positioning is a separate piece of writing.
10. **Per-thread privacy / reuse policy (flagged in Q4).** A thread-level toggle controlling whether participants may reuse information captured or inferred inside the thread beyond the thread's boundaries. F1 does not pre-empt this; sequence under D Stage 4 memory contract once the storage shape settles.
11. **Web Portal URL backwards-compat story for `/conversations/{id}` reads.** Q6 + Q10 leave the legacy URL frozen for archival reading; whether the canonical surface for new threads becomes `/threads/{id}` (with `/conversations/{id}` aliased) is C2's call with an explicit deprecation window. File when C2 picks up the URL story.
