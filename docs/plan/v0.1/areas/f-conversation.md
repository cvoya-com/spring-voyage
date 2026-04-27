# Area F: Conversation concept (#1123)

**Status:** 🟢 **Planning done.** Three sub-issues created (#1268, #1271, #1273). #1085 and #1086 closed as superseded by #1123. Next: work F1 system design (#1268) — all other F work is blocked on it.

## Sub-issues (v0.1)

| # | Title | Status |
|---|---|---|
| [#1268](https://github.com/cvoya-com/spring-voyage/issues/1268) | F1: Conversation participant-set model — system design | 🔵 Open; anchor for all F work |
| [#1271](https://github.com/cvoya-com/spring-voyage/issues/1271) | F2: Update docs/glossary.md, docs/architecture/messaging.md, revise ADR-0018 | 🔵 Open; blocked by #1268 |
| [#1273](https://github.com/cvoya-com/spring-voyage/issues/1273) | F3: New ADR for conversation-as-participant-set + dialog/task UX model | 🔵 Open; blocked by #1268 |

Execution-plan issue is **deliberately deferred** until F1 converges. Implementation issues follow the execution plan.

## Reframing anchor

[#1123](https://github.com/cvoya-com/spring-voyage/issues/1123) is the conceptual anchor. Key decisions already made:

- "Conversation" → **participant-set relationship** (the participant set IS the identity)
- Users see: a **dialog surface** (one per relationship with an agent, like iMessage DMs) + an **ambient task surface**
- No "new conversation" button, no thread picker, no session list
- Per-conversation mailbox; memory has two layers (per-conversation + agent-level spanning); cross-conversation flow is policy-governed

F1 (#1268) must resolve the 10 open questions from #1123 (naming, container/execution model, dispatch semantics, memory flow, participant-set change UX, initiative messages, misinference correction, cold start, multi-party, migration) before implementation can begin.

## Dependencies

- Depends on: J (ADR audit) ✅ done.
- Blocks: C2, E1, E2 (architecturally).
- Intersects with: D (execution model, boundary implications).

## Closed as superseded by #1123

- ~~#1085~~ AgentActor mailbox conflates message arrival/execution serialization
- ~~#1086~~ UI: no surface for sending a message to an existing conversation
