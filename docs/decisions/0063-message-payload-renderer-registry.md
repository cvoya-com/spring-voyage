# 0063 — Message-payload renderer registry

- **Status:** Accepted (2026-05-27). v0.1 work — implementation lands in the same PR as this record.
- **Date:** 2026-05-27
- **Related ADRs:** [0060](0060-participant-set-agent-api-and-structured-envelope.md) — pins the structured *inbound* envelope shape but does not pin the *text rendering* of `Message.Payload` for consumer surfaces. This record fills that gap; ADR-0060 stays load-bearing for the inbound user-message slot.
- **Related code:** `src/Cvoya.Spring.Core/Messaging/Rendering/`; `src/Cvoya.Spring.Dapr/Actors/MessageArrivedDetails.cs`; `src/Cvoya.Spring.Dapr/Threads/EfMessageWriter.cs`; `src/Cvoya.Spring.Connector.Slack/Outbound/SlackOutboundDispatcher.cs`; `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`.
- **Related issues:** [#2843](https://github.com/cvoya-com/spring-voyage/issues/2843) — surface, decision, and acceptance criteria for this record; [#2818](https://github.com/cvoya-com/spring-voyage/issues/2818) — original Slack outbound dispatch that surfaced the contract gap; [#2767](https://github.com/cvoya-com/spring-voyage/issues/2767) — `sv.messaging.send` content-shape wrap; [#1547](https://github.com/cvoya-com/spring-voyage/issues/1547) / [#1549](https://github.com/cvoya-com/spring-voyage/issues/1549) — dispatcher's `Output`/`ExitCode` wrap; [#1209](https://github.com/cvoya-com/spring-voyage/issues/1209) — original timeline body extraction.

## Context

Three+ sites in v0.1 take a `Message.Payload` (a `JsonElement`) and render it as plain text:

- The conversation timeline (`MessageArrivedDetails.TryExtractText` + `EfMessageWriter` for the persisted `messages.body` column).
- The Slack outbound dispatcher (`SlackOutboundDispatcher.ExtractMessageText` for the `chat.postMessage` body).
- The A2A reasoning-trace path (`A2AExecutionDispatcher.ExtractTextFromTask` / `ExtractTextFromParts` — produces a diagnostic string, not a `Message.Payload` rendering).

The first two operate on `Message.Payload` and disagreed by accident:

| Shape | Pre-#2843 timeline | Pre-#2843 Slack |
|-------|--------------------|------------------|
| bare JSON string | ✅ verbatim | ✅ verbatim |
| `{ "Output": "...", "ExitCode": 0 }` | ✅ returns `Output` | ❌ raw JSON |
| `{ "content": "..." }` | ✅ returns `content` | ❌ raw JSON |
| `{ "text": "..." }` | ❌ neutral placeholder / empty | ✅ returns `text` |
| `{ "body": "..." }` | ❌ neutral placeholder / empty | ✅ returns `body` |

A payload that named the "wrong" property silently rendered as raw JSON in Slack or as an empty bubble in the timeline. ADR-0060 pinned the structured *inbound* envelope shape (the agent's user-message slot) but said nothing about how downstream consumers should render `Message.Payload` back into text — so each consumer rolled its own probe, and they only agreed where the v0.1 producer happened to emit a shape both recognised.

## Decision

A consumer-side `IMessagePayloadRenderer` registry, owned by `Cvoya.Spring.Core.Messaging.Rendering`, replaces the three inline probes. Every site that needs a text view of a `Message` resolves `IMessagePayloadRendererRegistry` and calls `TryRender(message)`; the platform ships a fixed set of built-in renderers covering today's well-known shapes.

**Selection contract.** A renderer declares an optional `TargetType` (a `MessageType?`; `null` = any) and an integer `Priority`. The registry filters by `TargetType` first, then walks remaining renderers in descending `Priority` order and returns the first non-null `Render` from a renderer whose `CanRender(message)` returns `true`. Multiple renderers may claim the same shape; priority resolves ties deterministically.

**Built-in renderers (priority shown):**

| Renderer | Matches | Priority |
|----------|---------|---------:|
| `BareStringPayloadRenderer` | payload is a JSON string | 100 |
| `TextPropertyPayloadRenderer` | object with top-level string `text` | 80 |
| `BodyPropertyPayloadRenderer` | object with top-level string `body` | 70 |
| `OutputPropertyPayloadRenderer` | object with top-level string `Output` | 60 |
| `ContentPropertyPayloadRenderer` | object with top-level string `content` | 50 |

When no renderer claims the payload, the registry returns `null`. The two production consumers handle `null` differently — and that asymmetry is intentional:

- The conversation timeline (`MessageArrivedDetails.Build`) drops the `body` field; downstream readers fall back to `payload`.
- The Slack outbound dispatcher (`SlackOutboundDispatcher`) falls back to `payload.GetRawText()` so the bound user sees the structured shape rather than an empty Slack message.

**A2A reasoning-trace stays separate.** `A2AExecutionDispatcher.ExtractTextFromTask` operates on the A2A SDK's `AgentTask` / `AgentMessage` / `Part` types, not on `Message.Payload`; its output feeds `RuntimeOutcome.ReasoningTrace` (diagnostic), not a `Message`. Folding it into the renderer registry would require a producer-side conversion of `AgentTask` into a Message envelope before extraction — out of scope for this record. The convergence is filed as a follow-up.

## Alternatives considered

- **Pin a canonical text field on `Message.Payload` (option 1 in #2843).** Rejected for v0.1. Requires every producer — the author SDK, the A2A bridge, every inbound connector — to emit the chosen field. Connector payloads from external systems (Slack events, future webhook events) come in with their own shape; we would either wrap them ourselves (which is what the registry already does, dressed differently) or lose the structured form. Cheaper to ship; harder to retrofit at the producer surface.

- **Pure shape probing without a `TargetType` key.** Considered. With today's renderers — all `TargetType = null` — the key adds no filtering value, only an aspirational seam. Kept because it is one extra field on the interface and lets a future MessageType-specific renderer (e.g. a `HealthCheck` payload structured-status renderer) opt into a single type without redesigning the registry; the cost is one nullable property per renderer.

- **Explicit `payload.kind` discriminator (option 2 in #2843).** Rejected. The framing of "producers self-declare via `kind: "chat.text" | …`" only holds for producers Spring Voyage controls. A material fraction of payloads originate outside that boundary — A2A peer responses, inbound Slack / GitHub webhook events, future external connectors — and those producers don't know about our discriminator. The platform would wrap them at the boundary regardless, which is the same shape probing the registry does today, just dressed differently. For the producers we do own, the shape variation is small (five known shapes) and ambiguous payloads don't exist in practice. The probe-based registry is honest about the consumer / external-producer asymmetry; revisit only if a concrete ambiguous-payload incident or producer-side debugging pain surfaces.

- **Static `MessageArrivedDetails` helper, parameterised.** Pre-PR shape. Rejected because keeping it static forced every caller to pass the registry — that pollutes the actor and writer call sites without buying anything over an injectable instance. The helper now holds the registry; production DI wires a singleton; legacy test harnesses use `MessageArrivedDetails.Default`, which builds a registry over the platform's built-in renderer set (overlay-added renderers are not visible from `Default`).

## Consequences

### Simpler

- One canonical extractor across the timeline, the persistence column, and Slack outbound. New consumers (future connectors that post to external surfaces) opt into the same surface with a single DI dependency.
- The five shape probes are testable in isolation — each renderer is a small pure class with its own `CanRender` / `Render`.
- Slack and the timeline now both recognise every well-known shape: a `text`-shaped payload renders inline in the timeline; an `Output`-shaped payload renders in Slack. The pre-#2843 "agreed by accident" matrix collapses to one row per shape.

### Harder

- One more singleton on the messaging side and one optional ctor parameter on each of `AgentActor` / `HumanActor` / `UnitActor` (the helper itself, resolved through DI; `null` falls back to `MessageArrivedDetails.Default` so the 20+ direct test instantiations don't change).
- Adding a new well-known shape now requires a renderer class + a DI registration. The pre-PR shape allowed a one-line edit to a static method; the registry pattern is heavier per addition. Acceptable cost for the structural fix.

### What this implies

- **Migration.** None. v0.1 is pre-1.0 and the "no back-compat shims" norm applies. The three pre-#2843 inline probes are deleted; the registry's coverage is a strict superset.
- **Future work (deferred).** Converge the A2A reasoning-trace extractor with the renderer registry by routing A2A responses through a `Message`-shaped intermediate. No producer-side `kind` migration is scheduled (see the alternative above) — the probe-based registry is the durable shape.
