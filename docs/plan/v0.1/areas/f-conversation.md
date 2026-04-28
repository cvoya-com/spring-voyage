# Area F: Conversation concept

**Status:** 🟢 **Planning done.** Sub-issues, supersessions, and live work breakdown live on umbrella [#1220](https://github.com/cvoya-com/spring-voyage/issues/1220) — see its sub-issue panel.

## Reframing anchor

The conceptual anchor for this area is the participant-set reframing — the system-level concept formerly called "conversation":

- The system stores a **Thread** — the unique, persistent record for a participant set (the participant set IS the identity).
- The product presents it as an **Engagement** — the enduring relationship surface that users navigate to, like an iMessage DM.
- The user works inside a **Collaboration** — the active workspace where the engagement happens, including the task panel. No "new conversation" button, no thread picker, no session list.
- Per-thread mailbox; memory has two layers (per-thread + agent-level spanning); cross-thread flow is policy-governed.

The system-design sub-issue (tracked on the umbrella) resolves the open questions — naming, container/execution model, dispatch semantics, memory flow, participant-set change UX, initiative messages, misinference correction, cold start, multi-party, migration — before implementation begins. The execution-plan issue is **deliberately deferred** until system design converges.

> **Terminology:** the three terms — **Thread** (system / architectural), **Engagement** (UX product narrative), **Collaboration** (UX active workspace) — are defined canonically in [`docs/architecture/thread-model.md`](../../../architecture/thread-model.md) and the [glossary](../../../glossary.md). Use those anchors in downstream artefacts.

## Dependencies

- Depends on: J (ADR audit) ✅ done.
- Blocks: C2, E1, E2 (architecturally); Area D Stage 4 (memory contract).
- Intersects with: D (execution model, boundary implications).
