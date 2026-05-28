# 0064 — Conversation participants and platform-addressed continuation

- **Status:** Accepted (2026-05-28). Design approved; implementation is tracked under [#2889](https://github.com/cvoya-com/spring-voyage/issues/2889) (messaging e2e review) and [#2887](https://github.com/cvoya-com/spring-voyage/issues/2887) (multi-party send) and does **not** land with this record — this is a docs-only PR.
- **Date:** 2026-05-28
- **Related ADRs:** [0060](0060-participant-set-agent-api-and-structured-envelope.md) — **extends**: adds the `participants` field to the inbound envelope and a continuation tool; 0060's `from`/`to` semantics are preserved unchanged. [0030](0030-thread-model.md) — participant-set thread identity. [0049 (archived)](archive/0049-message-delivery-tool-contract.md) — `sv.messaging.send` is a one-way delivery acknowledgement, not a synchronous response (load-bearing for the framing here). [0053](0053-units-are-agents-and-one-way-delivery.md) — units are agents; the platform delivers one-way messages. [0056](0056-tool-only-side-effects.md) — tool calls are the only side-effect channel.
- **Related code (implementation pending, per #2889):** `src/Cvoya.Spring.Dapr/Prompts/InboundEnvelopeBuilder.cs`; `src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs`; `src/Cvoya.Spring.Dapr/Messaging/{MessagingToolHandlers,MessageDeliveryService}.cs`; `src/Cvoya.Spring.Core/Messaging/{Message,Address}.cs`.
- **Related issues:** [#2887](https://github.com/cvoya-com/spring-voyage/issues/2887) — human multi-party send collapses to per-recipient threads (the defect whose fix depends on this contract); [#2889](https://github.com/cvoya-com/spring-voyage/issues/2889) — messaging e2e review (umbrella); [#2747](https://github.com/cvoya-com/spring-voyage/issues/2747) — hide `thread_id`, participant set as the agent primitive (precedent).

## Context

[ADR-0060](0060-participant-set-agent-api-and-structured-envelope.md) made the participant set the agent-facing primitive and pinned a structured inbound envelope (`from`, `to`, `message_id`, `timestamp`, `payload`; no `thread_id`). `to` carries "the participants the sender targeted, with the recipient's own address among them" — so for a delivery from `agent1` to `{agent2, agent3}`, each recipient sees `from: agent1` and `to: [agent2, agent3]`.

Two gaps surfaced in use — one of them a shipped defect ([#2887](https://github.com/cvoya-com/spring-voyage/issues/2887)):

1. **Continuing a conversation forces an inference the runtime gets wrong.** To address everyone already on a conversation, an agent must reconstruct the set as `from ∪ (to − self)` and hand it to `send`. That union-and-subtract is error-prone: in practice a runtime addressed only the original sender, dropped the co-recipients, and split one conversation across two participant sets (#2887). The envelope gave the agent the pieces but not the assembled set, and nothing offered to address "whoever is already here" on its behalf.

2. **`from` and `to` are different domains, and the envelope doesn't say so.** A sender may be non-routable — a `connector:` address is provenance only; "nothing is ever routed back to it" (ADR-0053 §5), and addressing one returns `UnroutableTarget` (ADR-0060). The sender is therefore not, in general, a member of any set the agent could address, and the envelope must not invite the agent to treat `from` as an addressable peer.

This record extends ADR-0060's envelope and tool surface to close both — without re-exposing `thread_id`, and without implying that messaging is synchronous request-and-answer (ADR-0049: `send` is a one-way delivery acknowledgement).

## Decision

**1. The envelope gains a `participants` field — the addressable roster of the conversation.**

`participants` is the set of **routable** members of the conversation: `to` together with the sender when the sender is itself routable. It always includes the receiving agent; it never includes a non-routable origin (a `connector:` sender appears in `from` only). For `agent1 → {agent2, agent3}`, every recipient sees:

```
- from: agent1
- to: [agent2, agent3]
- participants: [agent1, agent2, agent3]
- message_id: <uuid>
```

The relationship is `participants = to ∪ ({from} if from is routable)` — "everyone you could reach here." `to` is unchanged from ADR-0060 (the targeted recipients, receiver included, sender excluded); `participants` is the assembled roster the agent would otherwise have to compute.

`participants` is information, not instruction. An agent is free to address the whole set, a subset, or someone else entirely; the field exists so that choice is made over explicit data rather than an inferred union.

**2. A new tool addresses the existing participant set: `sv.messaging.respondTo(message_id, content)`.**

`respondTo` delivers `content` to the participants of the conversation that `message_id` belongs to, minus the caller. The agent names a `message_id` it already holds from the envelope — never a `thread_id` (#2747: internal ids do not cross the agent boundary). The platform resolves the message to its conversation, takes that conversation's current routable participant set, and delivers one-way to each member on the same conversation.

| Tool | Recipients chosen by | Use |
|------|----------------------|-----|
| `sv.messaging.send(recipients[] \| scope, content)` | the **agent** (explicit) | a new conversation, a subset, or adding someone |
| `sv.messaging.respondTo(message_id, content)` | the **platform** (the conversation's participants) | continuing the conversation a message belongs to |

`respondTo` is not a synchronous answer and carries no request/response coupling — it is one-way delivery (ADR-0049 / 0053) addressed to a set the platform computes. Because `message_id` is durable, `respondTo` also serves a *later* continuation: an agent that kept a `message_id` can continue that conversation at any time, without having tracked its participants.

This turns continuing a conversation into a single deterministic call instead of a reconstructed recipient list — the structural fix for the split in #2887. An agent that *does* choose to address the roster explicitly through `send` passes `participants` (the platform dedupes self); passing `to` would omit the original sender, which is exactly the omission `participants` exists to prevent.

## Alternatives considered

- **Put the sender in `to`.** Rejected — the connector case is decisive. `from` admits non-routable origins (`connector:`); `to` / `participants` are strictly routable. "The sender is also a recipient" is false for every connector-originated message, so it cannot be an invariant. `from` (origin) and `to` (recipients) are different domains; conflating them breaks the moment a sender is a kind that cannot be addressed.
- **Strip the receiving agent from `to` / `participants` (show only "the others").** Rejected. It makes the envelope asymmetric — each recipient sees a different list — for no gain; "is one of these me?" is trivial since the agent knows its own address. One `to` and one `participants`, identical for every recipient, is simpler to produce and to reason about (and matches ADR-0060's existing "recipient's own address among them").
- **Expose `participants` only, no continuation tool.** Rejected. It leaves the common case — continue with everyone here — as the same union-and-subtract inference that caused #2887, merely with the union pre-supplied. The deterministic path (`respondTo`) is the point.
- **Add the continuation tool only, no `participants` field.** Rejected. `respondTo` handles "everyone here," but the agent still needs the roster as data to choose a *subset* or to redirect — the explicit decisions we want to support. The two are complementary: `participants` informs the choice; `respondTo` is the zero-enumeration default.
- **Name the continuation tool `reply` / `replyAll`.** Rejected (naming). Both imply a synchronous request-and-answer model; Spring Voyage messaging is one-way delivery (ADR-0049 / 0053), so both the prose and the verb avoid "reply". `respondTo` reads as "address the conversation this message belongs to"; keyed on a durable `message_id`, it also covers a later continuation, so a separate `followUp` verb is unnecessary.
- **`participants` is derivable, therefore omit it.** Considered honestly: today `participants = to ∪ {routable from}`, so it carries no information `from` + `to` don't already hold. Kept anyway because (a) the implicit derivation is exactly what the runtime got wrong (#2887), and (b) it is the natural carrier if the model later grows silent observers / CC members — participants that are neither `from` nor in `to`.

## Consequences

### Simpler

- Continuing a conversation is one call (`respondTo(message_id, content)`) over a platform-computed set — no recipient reconstruction, no `thread_id`.
- The envelope answers "who is on this conversation?" directly (`participants`) instead of asking the agent to assemble it.
- `from` / `to` keep ADR-0060's meaning unchanged; this is purely additive.

### Harder

- One more envelope field and one more tool for agent authors to learn; `to` vs `participants` needs a one-line gloss in the prompt ("`participants` = everyone here; `to` = who this was addressed to, sender excluded").
- The dispatcher computes `participants` (a routable filter over the resolved participant set) during inbound envelope assembly — a cheap read, but another step.

### What this implies

- **Implementation** is tracked under #2889 (and #2887), not bundled here. The human send path and the agent path converge on resolving a thread from the full participant set; the `participants` field and `respondTo` land as part of that work, and the architecture docs (`docs/architecture/messaging.md`, `docs/concepts/messaging.md`) are updated then.
- **Migration.** None; v0.1 is pre-1.0 ("no back-compat shims"). The envelope addition is additive; `respondTo` is new surface.

## Revisit criteria

- If silent observers / CC (members who see a conversation without being addressed) are introduced, re-confirm that `participants` is the right carrier and whether `to` needs a companion (e.g. a separate observer list).
- If a concrete need for synchronous request/response (await a specific message) ever appears, this record's one-way framing is what to revisit.
