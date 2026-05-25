# 0060 — Participant-set agent API and structured inbound envelope

- **Status:** Accepted (2026-05-24). v0.1 work — implementation lands in the same PR as this record.
- **Date:** 2026-05-24
- **Related ADRs:** [0030](0030-thread-model.md) — participant-set thread identity (load-bearing reframing this record extends to the agent-facing API); [0049 (archived)](archive/0049-message-delivery-tool-contract.md) — `sv.messaging.send` returns a delivery acknowledgement, not a reply (preserved); [0054](0054-one-mcp-server-one-execution-host.md) — single platform MCP server (preserved); [0056](0056-tool-only-side-effects.md) — side effects happen only through tool calls (the silent-runtime failure pattern this record's envelope addresses at the input boundary); [0059](0059-prompt-assembly-pipeline.md) — prompt-assembly pipeline (the inbound-messages section in the platform contract is updated by this record).
- **Related docs:** [`docs/architecture/messaging.md`](../architecture/messaging.md) (updated by this record); [`docs/concepts/messaging.md`](../concepts/messaging.md), [`docs/concepts/agents.md`](../concepts/agents.md).
- **Related code:** `src/Cvoya.Spring.Dapr/Messaging/{MessagingToolHandlers,MessageDeliveryService,MessageDeliveryException}.cs`; `src/Cvoya.Spring.Dapr/Skills/{SvMessagingSkillRegistry,SvMemoryHistoryRegistry}.cs`; `src/Cvoya.Spring.Dapr/Prompts/{InboundEnvelopeBuilder,PlatformPromptProvider}.cs`; `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`.
- **Related issues:** [#2746](https://github.com/cvoya-com/spring-voyage/issues/2746) — structured inbound envelope; [#2747](https://github.com/cvoya-com/spring-voyage/issues/2747) — hide `thread_id` from agents, participant set as the primitive; [#2703](https://github.com/cvoya-com/spring-voyage/issues/2703) — silent-runtime-on-casual-input failure pattern this record's envelope addresses at the input boundary; [#2740](https://github.com/cvoya-com/spring-voyage/issues/2740) — connector-as-sender semantics this record makes explicit at the agent surface.

## Context

[ADR-0030](0030-thread-model.md) settled that a thread is uniquely identified by its *participant set*: there is exactly one thread per set, and adding or removing a participant produces a different thread. That decision was honoured internally — `IThreadRegistry.GetOrCreateAsync(participants)` canonicalises the set, the EF schema keys on the resulting id, mailbox FIFOs and hop counters partition on it — but it was not extended to the agent-facing API. The messaging surface still required a `thread_id` (and `sv.messaging.send(address, message)` named one recipient at a time); a separate `sv.thread.*` family let the agent inspect threads by `thread_id`. Agents therefore had to discover and pass an identifier whose meaning was bookkeeping, not domain.

Two related failure patterns surfaced under that shape.

First, [#2703](https://github.com/cvoya-com/spring-voyage/issues/2703) documented a silent-runtime-on-casual-input failure: when a human sent the text `"hello"` to an agent, the runtime saw a chat-shaped user message and replied as text on stdout. The platform recorded a `RuntimeCompletedSilent` activity; no participant heard the reply. The platform contract (in the assembled system prompt) carried prose telling the agent to call `sv.messaging.send` instead of writing to stdout — but the prose was fighting the input format. Chat turns are textually answered; the user-message slot literally contained just `"hello"`, and "send a tool call" was a non-default response shape to ask of an instruction-tuned model that had just been handed a chat turn.

Second, the agent's mental model carried a load-bearing concept (`thread_id`) that nothing in the domain required. Every reply, every history fetch, every thread inspection threaded an opaque id around. The platform owned the id; the agent had to keep up. When the participant set was small ("just reply to the human who sent this"), the id felt redundant; when it was large (a unit sending to several siblings), the id obscured the actual decision (who is on this conversation?).

This record extends ADR-0030's reframing to the agent-facing API and addresses the input-shape problem in the same pass.

## Decision

Two changes, intentionally landed together because they reshape the same agent-facing surface.

**1. The agent-facing primitive is the participant set, not `thread_id`.**

The messaging tools and the shared-memory tools all take a `recipients` (or `participants`) list, never a `thread_id`. The calling participant is auto-included in every participant set, so the agent does not list itself. The platform derives `thread_id` internally per ADR-0030 — it remains the canonical storage key for `ThreadActor`, `ThreadHopActor`, the mailbox, and the activity log, but it never crosses the agent boundary.

| Surface | Pre-#2747 | Post-#2747 |
|---------|-----------|------------|
| `sv.messaging.send` | `send(address, message)` — one target | `send(recipients[] \| scope, message)` — every recipient on a single SHARED thread `{caller} ∪ recipients` |
| `sv.messaging.multicast` | `multicast(addresses[] \| scope, message)` — N targets in parallel on per-pair threads | `multicast(recipients[] \| scope, message)` — same parallel fan-out, semantics unchanged |
| Inspecting threads | `sv.thread.{list, get, search, participants}` keyed on `thread_id` | `sv.memory.{engagements, history_with(participants[]), search_messages(query, participants[]?)}` — all participant-set-shaped |
| Connector recipients | Accepted; would fail later | Rejected synchronously with `UnroutableTarget` reject code (connectors are senders only) |

The `send`-vs-`multicast` split is preserved because they encode a routing decision the platform cannot infer from the input alone: `send([A, B])` means "every recipient should know about the others"; `multicast([A, B])` means "each gets a 1-1 channel". The decision belongs to the caller; the tool surface makes it explicit by keeping the two verbs distinct while collapsing their input shapes.

`sv.thread.*` is retired entirely. Its read surface (`list`, `get`, `search`) re-emerges as participant-set-shaped tools under `sv.memory.*`; `participants` is dropped because the participant set IS the input — there is nothing the tool would return that the caller did not supply.

The `sv.memory.*` namespace now hosts two distinct surfaces: the agent's private memory (existing `add` / `get` / `list` / `search` / `update` / `delete`) and the shared participant-set timelines added here (`engagements` / `history_with` / `search_messages`). The names do not collide. A future cleanup may split the two into separate namespaces; this record intentionally defers that to avoid an additional rename in v0.1.

**2. The inbound message is a structured envelope, not a raw payload string.**

The dispatcher's `A2AExecutionDispatcher.SendA2AMessageAsync` wraps the inbound payload in a fixed-shape envelope before placing it in the runtime's user-message slot. The envelope is rendered as bullet header + fenced JSON appendix so a structured payload (a webhook event from a connector, a custom shape from a peer) survives intact:

```
You received a message.

- from: human:<32-hex> (Alice)
- to: [agent:<32-hex>, unit:<32-hex>]
- message_id: <uuid>
- timestamp: <iso-8601>
- payload:

hello

```json
{ "from": "human:...", "from_display_name": "Alice", "to": [...], "message_id": "...", "timestamp": "...", "payload": "hello" }
```

Decide what to do. To send a message in response, call `sv.messaging.send` with the recipient address(es) and body.
```

Field semantics:

- `from` carries the sender's canonical address; when the directory has a row, the display name appears in parentheses (connectors typically don't have a row — the address-only render is correct for them).
- `to` carries the *participants the sender targeted*, with the recipient's own address among them. For a `send` to multiple recipients the agent sees the full set; for a `multicast` or a 1-1 `send` the agent sees only itself.
- `thread_id` is **not** present — the agent never names it.
- The fenced JSON appendix carries the payload verbatim, so a connector payload (e.g. a GitHub webhook event) survives the user-message slot without text-coercion loss.

The envelope is rendered for every runtime, not just the CLI bridges: the A2A-native `SpringVoyageAgentLauncher` consumes the same envelope text through the same A2A `message/send` user-message slot. This keeps the dispatcher path uniform and matches the issue's intent of making the platform's view of the world explicit at the input boundary.

**Connector participants.** A `connector://` address is a legitimate member of a participant set for thread-identity purposes — it stamps message provenance on inbound webhook events. `sv.memory.history_with([connector:<id>, …])` returns the timeline that includes those events. But routing TO a connector returns `UnroutableTarget` synchronously; this gives the calling model an actionable error rather than a silent failure.

## Alternatives considered

- **Keep `thread_id` exposed; fix only the envelope shape.** Rejected. The two failures are coupled at the input boundary: a runtime that sees a chat-shaped user message AND has to thread a `thread_id` back through every tool call has two off-ramps from doing the right thing. Reshaping the envelope without reshaping the tools leaves the agent paying the same bookkeeping cost.

- **Collapse `multicast` into `send` (single tool, two modes via flag).** Rejected. The routing decision (shared vs separate threads) is the only meaningful difference; encoding it as a flag rather than a verb pushes it from "obvious from the tool name" to "obvious from a flag value." The two tools take the same input shape now — naming them differently is the lowest-cost way to make the decision visible.

- **Rename agent-private memory to free up the `sv.memory.*` verbs.** Considered (e.g. `sv.memory.private.*` for the agent-owned store). Rejected for v0.1: the participant-set tools use distinct verbs (`history_with`, `engagements`, `search_messages`) that don't collide with the private-memory verbs (`add`, `get`, `list`, `search`, `update`, `delete`). The cleanup can land later without a back-compat shim because v0.1 is pre-1.0; carrying the rename now would expand this PR's blast radius without buying clarity the distinct verb names don't already provide.

- **Render the envelope as JSON only (no bullet header).** Rejected. Instruction-tuned models pattern-match on Markdown-shaped headers; the bullets make the structured fields salient without trusting the model to parse JSON in its head. The fenced JSON appendix is for the model to lift the payload verbatim when it needs to.

- **Render the envelope only for CLI bridges; pass bare payload to the A2A-native Python agent.** Considered (the issue's literal wording suggested it). Rejected. The dispatcher path is shared; branching by launcher kind adds a special case for one runtime. Uniform envelope across runtimes is the simpler, more durable shape.

- **Drop scope-based multicast entirely.** Considered (consistent with "aggressive cleanup, no back-compat"). Rejected after design discussion: the scope variant carries a domain decision (`siblings` = "everyone on my team") that's painful to recompute on every call; keeping it spares the agent a `sv.directory.list` round-trip on every fan-out.

## Consequences

### Simpler

- Agents form messages in terms of recipients (names from the directory) rather than recipients + an opaque id.
- The thread-inspection surface drops from four tools (`sv.thread.list/get/search/participants`) to three (`sv.memory.engagements/history_with/search_messages`) — `participants` is redundant when the participant set is the input.
- The silent-runtime failure pattern (#2703) has a structural answer: the envelope frames every inbound message as a structured event to act on, not a chat turn to text-answer.

### Harder

- The dispatcher's per-turn cost grows by two reads (`IDirectoryService.ResolveAsync(sender)` for display name, `IThreadRegistry.ResolveAsync(threadId)` for the participant set). Both are cheap reads against already-cached infrastructure but are not free; the envelope-resolver opens one DI scope per call to avoid coupling the dispatcher singleton to scoped collaborators.
- Two agent-facing memory namespaces (private memory vs shared timelines) share `sv.memory.*` and are distinguished only by verb. A future rename is acceptable; until then, agent authors and prompt writers need to read tool descriptions to know which surface they're on.

### What this implies

- **Migration.** None. v0.1 is pre-1.0 and the project's "no back-compat shims" norm applies. Existing agent instructions naming `sv.thread.*` or `sv.messaging.send(address=…)` break by design and are updated to the new shape in the same pass.
- **Future feature (deferred).** An escape-hatch flag on `sv.memory.history_with` that lets a permitted caller query a participant set it is not part of. Scoped out of v0.1 per the issue's design discussion; the v0.1 surface does not paint us into a corner because the participants array is unordered (a future `include_caller=false` flag drops in cleanly).
- **Namespace cleanup (deferred).** Splitting `sv.memory.*` into the agent's private store and the participant-set shared timelines is a future cleanup, deliberately not bundled here.
