# Threads, Engagements, and Collaborations

A **thread** is the canonical record of what happens between a specific set of participants. The participant set IS the identity: there is exactly one thread per unique participant set. Add or remove a participant, and you have a different thread.

This doc explains three related but distinct terms — **Thread** (system), **Engagement** (product), **Collaboration** (active workspace) — and when to use each.

## Three Layers, Three Terms

The system / product / working-UI split is clean:

- **Thread** — the system / architectural concept. Used in code, schema, and APIs. The unique, persistent, system-level record for a set of two or more participants, containing their lifelong shared exchanges and activity.

- **Engagement** — the product / UX narrative term. The ongoing shared context between participants over time. Used in product navigation, lists, and relationship continuity copy. Presented to the user as a persistent connection worth revisiting.

- **Collaboration** — the active workspace / working surface. The active shared space where participants converse, coordinate, and get work done. Used for the active teamwork, tasks, decisions, plans, and execution. What the user opens and works in.

**The mapping is direct: the system stores a thread; the product presents it as an engagement; the user works inside a collaboration.**

A user opens a collaboration to work; the system records it under the relevant thread; they revisit it later as one of their engagements.

## Practical Guide — Where to Use Which Term

| Layer | Term | Example Phrasing |
|-------|------|------------------|
| Code, schema, architecture, APIs | **Thread** | "Find the thread", "Append to the thread", "Thread lookup is keyed by normalized participant set" |
| Product navigation / lists | **Engagement** | "Recent engagements", "Continue this engagement", "This engagement has new activity" |
| Active workspace / main screen | **Collaboration** | "Open collaboration", "Resume collaboration with your writing agent", "This collaboration has 3 participants" |

## Engagement vs. Collaboration — The UX Split

- **Engagement** = the enduring connection. Use for relationship narrative — "the ongoing shared journey", continuity over time, something broader than messaging alone. The thing you come back to.

- **Collaboration** = the active manifestation. Use for active teamwork, tasks/decisions/plans/execution, the space the user opens and works in today.

Example: a product feature might show "Recent engagements" in the navigation sidebar (the list of ongoing relationships), and when the user clicks one, they "open a collaboration" to work on that engagement's current tasks. If they add a participant to an existing engagement, the system creates a new underlying thread (different participant set), but the engagement surface absorbs the transition transparently — the user still sees one continuous relationship.

## Example Copy — System Voice vs. Product Voice

The same underlying thread appears differently in each layer:

### System/Architecture Voice
*Used in code comments, API docs, design rationale:*

> "The thread `{user, agent-A}` has accumulated 47 messages and 3 task state changes."

> "Thread lookup fails when the participant set is empty or contains only one member."

> "Store the new MemoryEntry with thread_id from the agent's operating context."

### Product/UX Voice
*Used in UI labels, user-facing docs, marketing:*

> "Your engagement with the writing agent is active. Open to resume."

> "Start a collaboration with the code-review agent."

> "2 new messages in your engineering-discussion engagement."

The underlying system concept — the participant-set identity, the append-only Timeline, the per-thread memory visibility — is the same. The words just match the layer.

## Cross-References

For more detail on each layer:

- **Glossary definitions:** [`docs/glossary.md`](../glossary.md) carries one-line definitions for Thread, Engagement, Collaboration, and related memory concepts.
- **Architecture and design rationale:** [`docs/architecture/thread-model.md`](../architecture/thread-model.md) covers the participant-set invariant, memory model, Timeline, participant-state machine, and answers ten specific design questions with rationale.
- **Durable decision:** [`docs/decisions/0030-thread-model.md`](../decisions/0030-thread-model.md) — the ADR capturing the architectural shape.

The thread model underpins how agents reason about work, how the platform tracks history and memory, and how users navigate their ongoing relationships with agents and teams.
