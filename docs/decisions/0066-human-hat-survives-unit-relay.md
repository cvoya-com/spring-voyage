# 0066 — A human's "hat" identity survives a unit/agent relay; the runtime never asserts a `human:` sender

- **Status:** Proposed (2026-06-01). Design spike for [#3001](https://github.com/cvoya-com/spring-voyage/issues/3001). This record converges the design and names the fix; implementation is split into the follow-ups listed under "What this implies" and does **not** land with this docs-only PR.
- **Date:** 2026-06-01
- **Related ADRs:** [0062](0062-tenant-user-human-explicit-binding.md) — **extends**: 0062 made `Message.From` always routable and put the `tenant-user → human` "hat" rewrite at the *API* boundary; this record extends the same invariant to the *runtime egress* (`sv.messaging.*`), where it is currently violated. [0053](0053-units-are-agents-and-one-way-delivery.md) — a unit IS an agent (`SPRING_AGENT_ID == SPRING_UNIT_ID`); the root of the "no own address" problem. [0047](0047-platform-user-human-split.md) — the `TenantUser` / `Human` split the hat model rests on. [0054](0054-one-mcp-server-one-execution-host.md) — the single worker-side MCP server and the per-turn session token; the per-turn delivery authority this record extends (consolidates the archived [0051](archive/0051-unified-platform-mcp-auth-model.md) / [0052](archive/0052-execution-host-roles-and-single-mcp-server.md)). [0064](0064-conversation-participants-and-continuation.md) — `participants` / `respond_to`; the continuation path that already preserves the human as a participant.
- **Related code:** `src/Cvoya.Spring.Dapr/Messaging/MessageDeliveryService.cs` (`DeliverWithRetryAsync` — the `From = caller` collapse), `src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs` (`ResolveCaller`, `BuildMessage`), `src/Cvoya.Spring.Dapr/Mcp/McpServer.cs` (`IssueSession`, `MaterialiseSubject`), `src/Cvoya.Spring.Core/Execution/IMcpServer.cs` (`McpSession`), `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs` (session minting), `src/Cvoya.Spring.Dapr/Prompts/InboundEnvelopeBuilder.cs` (`from` render), `src/Cvoya.Spring.Core/Messaging/Address.cs` (`IsRoutable`).
- **Related issues:** [#3001](https://github.com/cvoya-com/spring-voyage/issues/3001) (this spike); incident [#2977](https://github.com/cvoya-com/spring-voyage/issues/2977) (the magazine run that surfaced the collapse) and its open sub-issue [#2980](https://github.com/cvoya-com/spring-voyage/issues/2980); [#2419](https://github.com/cvoya-com/spring-voyage/issues/2419) (v0.2 — human identity resolution for `sv.*` tools); [#2878](https://github.com/cvoya-com/spring-voyage/issues/2878) (v0.2 — hide the unit/agent split at the runtime boundary); [#2972](https://github.com/cvoya-com/spring-voyage/issues/2972) (v0.2 — stale hats after unit deletion); [#1470](https://github.com/cvoya-com/spring-voyage/issues/1470) / [#1497](https://github.com/cvoya-com/spring-voyage/issues/1497) (v0.2 — humans as full unit members).

## Context

A real magazine-team run (incident #2977) surfaced a sender-identity collapse. Two distinct senders both reached members as `from: unit:<id>`:

1. **The unit agent itself** speaking (the Magazine Director's own runtime emitting a directive).
2. **A human acting through the unit** — a directive the human gave that the unit relayed onward.

Members could not tell which. In the incident this corrupted an edit: a message "appearing to come from the Director" reversed a Piece-3 ruling that the Director then disavowed. Members coped by hand-signing every message; the platform prompt itself (`PlatformPromptProvider`) tells the runtime that `from` is the canonical sender address — so a member reading `from: unit:<id>` *correctly* concludes "the unit said this," even when a human did.

### The hard invariant (the steering direction)

> A user's message must **never** surface to anyone as coming `from` a unit or an agent. A human's message always carries the human member's identity — the `human:<id>` "hat" that represents the human. User messages always come from a hat.

This is not a new principle. **ADR-0062 already decided it** — for the API boundary. `Message.From` must be one of the routable schemes (`agent` / `unit` / `human`); the authenticated principal is `tenant-user://`, which is non-routable, and the rewrite to the speaking-as `human:<id>` hat happens in `MessageEndpoints` via `ITenantUserHumanResolver.PickFromAsync`. The defect in #3001 is that **the same invariant is enforced at API ingress but not at runtime egress.** Once a human's message is inside a unit-agent's turn, nothing carries the human's hat back out when the unit re-emits.

### How `from` is stamped today

`Message.From` is set at three sites; only the first honours the hat invariant.

| Site | Code | `from` it produces |
|---|---|---|
| **API ingress** (human → unit/agent) | `MessageEndpoints` → `ITenantUserHumanResolver.PickFromAsync` | **`human:<id>`** (the hat). Correct. |
| **Runtime egress** (a runtime calls `sv.messaging.*`) | `SvMessagingSkillRegistry.ResolveCaller` → `MessageDeliveryService.DeliverWithRetryAsync` (`From = caller`) | **the runtime's own subject** — `unit:<id>` for a unit-agent, `agent:<id>` for a leaf. |
| **Connector inbound** | connector translators stamp a synthetic `connector:<id>` | provenance only (non-routable). |

The egress path is the leak. The caller is resolved from the **MCP session's own subject** and nothing else:

```
McpServer.IssueSession(agentId = SPRING_AGENT_ID, callerKind = message.To.Scheme)
  → McpSession.Subject = (callerKind, agentId)            // unit:<id> for a unit-agent
  → ToolCallContext { CallerId, CallerKind }
  → SvMessagingSkillRegistry.ResolveCaller(context)       // → unit:<id>
  → MessageDeliveryService: outbound = message with { From = caller }   // unit:<id>
```

The session carries `(token, agentId, threadId, callerKind, subject, messageId)` — **no human, no originator, no "acting-as" field anywhere on the MCP / A2A / messaging path.** So when a unit-agent relays a human directive onto a *new or different* thread, it has nothing to stamp but its own subject. Both senders collapse to `unit:<id>`.

### Why the continuation path is already safe (and the relay path is not)

The collapse only bites on a *new* participant set. ADR-0064's `sv.messaging.respond_to` continues the *same* thread: the human who started it is already a canonical participant of that thread, so the human's `human:<id>` is preserved as a participant and is who the conversation is "with." The break is the **relay/forward to a different recipient set** — the unit takes a human's instruction and `sv.messaging.send`s it to a member on a fresh `{unit, member}` thread. There the human is not (yet) a participant, and `From` collapses to `unit:<id>`.

### The unit-agent's own-identity problem (#3001 part 2)

Because **a unit is an agent** (ADR-0053), the unit-agent's container is launched with `SPRING_AGENT_ID == SPRING_UNIT_ID` (`AgentContextBuilder` stamps `EnvAgentId = launchContext.AgentId`, and for a unit the dispatch target *is* the unit). The unit-agent has **no separate "own address"** distinct from the unit it represents. So even setting the human case aside, a member cannot distinguish:

- a directive the **unit agent itself** authored, from
- a directive the unit is **relaying on behalf of** a human (or another member).

There is one identity (`unit:<id>`) doing double duty. A member authenticating "who actually told me to do this?" has no signal. This is the same structural gap the human case exposes, generalised: **the platform models the unit's *address* but not the *authorship* of a unit-emitted message.**

### The hat model and the "two humans in a clean Magazine install" question (#3001 part 3)

The hat model (ADR-0047 / ADR-0062): one `TenantUser` (the authenticated principal; OSS pins exactly one, `OssTenantUserIds.Operator`) is bound to N `Human` rows ("hats"), each with `humans.tenant_user_id` FK; `tenant_users.primary_human_id` pins the default outbound hat. `Address.IsRoutable` documents the contract directly: `tenant-user` is "resolved to a `human://` Hat before routing."

Where the two humans come from in a clean Magazine install:

1. **The default-tenant hat.** Created lazily the first time the OSS operator authenticates — `HumanIdentityResolver.ResolveByUsernameAsync` mints a `HumanEntity` keyed on the operator's username, bound to `OssTenantUserIds.Operator`, and the seed pass pins it as `primary_human_id`. This is "the default human for the default tenant, auto-associated with the tenant's default user" the steering comment names.
2. **The Magazine-declared hat.** The Magazine package declares a human member on the Director unit (`packages/magazine/units/magazine-director/package.yaml`: `- human: { roles: [approver] }`). On install, `OssPackageHumanResolutionPolicy` (post-ADR-0046 §10) **mints a fresh distinct `HumanEntity`** for that declaration — *not* the existing operator hat — also bound to the operator `TenantUser`. (Note: the issue's framing inherited from #2402 — "auto-fill with the install caller's UUID" — is **stale**; the policy was reshaped to mint a distinct hat per declaration. The `{human_id → user_id}` consolidation that would let one physical user fill multiple declarations with one hat is explicitly deferred to v0.2.)

So **two hats, one physical operator**: a generic operator hat and a Magazine "approver" hat. This is the same multi-hat reality #2972 reports (one user offered 6 hats with a single one-human unit) and the from-selector / per-hat inbox in ADR-0062 §5 already accommodates.

**Is two-humans correct?** Yes — *as a model*. A human plays distinct **team roles** (the Magazine "approver" / publisher) that are domain-meaningful and must be addressable independently of the generic operator identity; ADR-0062's hat model is built for exactly this. What is **not** correct today is the *ergonomics*: nothing tells the operator the two hats are the same person, stale hats survive unit deletion (#2972), and one physical user cannot yet collapse multiple declarations onto one hat (v0.2 `{human_id → user_id}`). Those are real but **already-tracked** problems, and they are orthogonal to the #3001 invariant. **The relay invariant must hold whether there is one hat or six** — the fix below does not depend on resolving the hat-count ergonomics, and must not wait on them.

The relationship to **#2419** (human identity resolution for `sv.*` tools) is adjacent, not central: #2419 is about resolving a human UUID to a *connector-native* id (e.g. a GitHub login) at a tool's egress. It shares the "the runtime should pass UUIDs, the platform resolves identity" principle this record relies on, but it does not address sender attribution on `Message.From`.

## Decision

### 1. The platform owns sender identity; a runtime may never *assert* a `human:` sender it did not receive

This is the security spine of the whole design and the reason the fix cannot be "let the unit-agent set `from`." If the messaging tools accepted a caller-supplied `from`, **any agent could forge any human's identity** — strictly worse than the current collapse. Sender identity is platform-stamped, derived from authenticated session state, exactly as `Message.From` is platform-stamped at the API boundary (ADR-0062 §3) and never accepted from the request body for the auth principal.

Therefore: the human's hat reaches a relayed message because **the platform carried it through the turn**, not because the runtime declared it.

### 2. Carry the originating human "hat" as per-turn delivery context, end to end

Extend the per-turn authority the MCP session already carries (ADR-0054 / archived 0051: `tenant`, `agentAddress`, `threadId`, `messageId`) with the **originating hat** of the turn:

- When a turn is dispatched in response to an inbound message whose `From` is `human:<id>`, the dispatcher records that hat as the turn's **originating human** on the MCP session (`A2AExecutionDispatcher` already has `message.From` in hand; `McpSession` gains an `OriginatingHuman: Address?` field; `ToolCallContext` carries it through).
- This is **provenance the platform observed**, never a value the runtime supplies — it is read off the inbound envelope the platform itself delivered, on the same session that already proves per-turn delivery authority.

### 3. A relayed human directive keeps the human as the sender (`from: human:<id>`), with the unit recorded as the relaying carrier — never as the author

When a unit-agent emits a message **on behalf of** the turn's originating human (the relay case), `Message.From` MUST be the human's hat, not the unit. The unit's involvement is recorded as **carrier provenance** (a `via` / relayed-by annotation on the envelope and the activity event), not as `from`. Two shapes were considered; the recommendation is (a), with (b) as the explicit fallback if (a) proves too coarse:

- **(a) Preferred — sender stays the human; unit is `via`.** The relayed message carries `from: human:<id>` (the originating hat) and a platform-stamped `via: unit:<id>` provenance field. A member reading it sees the human as the author and the unit as the conduit. This makes the envelope honest in one field (`from` = who the directive is from) and keeps the audit complete (`via` = who relayed it). It matches ADR-0062's dual-stamping instinct (`from.address` + `acting_tenant_user_id`) — render one identity, retain both.
- **(b) Fallback — compound sender.** If `via` proves insufficient (e.g. members need to address the relaying unit, not the human), model a first-class `(author, on_behalf_of)` pair on the envelope. Heavier; only if (a)'s single-`from`-plus-`via` cannot express a real need.

The runtime does not choose between "send as myself" and "relay as the human" by setting `from`. The platform decides from session state: a message a unit emits **within the same logical errand as an inbound human directive** is a relay (human `from`, unit `via`); a message the unit authors on its own initiative is the unit's own (`from: unit:<id>`, no human). The exact boundary of "same errand" is the one genuine design question left open (see §5).

### 4. Give the unit agent a distinguishable own-authorship signal (own-identity problem)

A unit-emitted message that is **the unit's own** (not a relay) must still be distinguishable from a relay at the member's envelope. With §3 in place this falls out for free: a relay reads `from: human:<id>, via: unit:<id>`; the unit's own message reads `from: unit:<id>` with no `via`. The presence/absence of `via` *is* the "did the unit author this or relay it?" signal the issue asks for — **without** minting a second address for the unit agent (which ADR-0053 deliberately avoids, and #2878 is separately collapsing `unit:`→`agent:` at the runtime surface anyway). Minting a distinct "unit-agent-self" address is **rejected**: it re-introduces the two-identities-per-unit split ADR-0053 removed and fights #2878.

### 5. Open design question deferred to implementation: the "same errand" boundary

The one judgement the platform must make is **when a unit-emitted message counts as relaying the inbound human directive vs. being the unit's own initiative.** Candidate rules, to be settled in the implementation issue (not here):

- **Turn-scoped (simplest):** every `sv.messaging.*` send *within the turn dispatched for a human inbound* is a relay of that human. Risk: over-attributes — a unit may legitimately author its own message in the same turn.
- **Thread-scoped:** a send is a relay only when the originating human is already a participant of the *upstream* thread the turn is handling. Aligns with ADR-0064's participant model; narrower.
- **Explicit-intent:** the runtime opts in (`sv.messaging.send(..., on_behalf_of_inbound=true)`) but the platform still supplies the *value* from session state — the runtime asserts "this is a relay," never *who*. Preserves §1 (no forgeable identity) while letting the runtime disambiguate its own two cases. **Tentatively preferred** because it removes the platform's need to guess intent without re-opening the forgery hole.

## Alternatives considered

- **Let the unit-agent set `Message.From` to the human's hat directly.** Rejected — decisively. A caller-supplied `from` lets any agent impersonate any human; this is the forgery hole §1 exists to close. Identity is platform-stamped from session state, never request-supplied — the same rule ADR-0062 applies to the auth principal.
- **Mint a separate "own address" for the unit agent (the issue's literal suggestion).** Rejected. It re-introduces the two-identities-per-unit split ADR-0053 deleted, adds an address with no directory/membership meaning, and works against #2878 (which is removing `unit:` from the runtime surface entirely). The `via`-presence signal in §4 distinguishes own-vs-relay without a second address.
- **Solve it in the prompt — tell members to hand-sign / distrust `from`.** Rejected. This is what the incident's members were *already forced to do*; it is the symptom, not a fix. The envelope must be trustworthy; pushing attribution into message bodies is exactly the failure #3001 documents.
- **Block the human case at the API — don't let humans message units at all, only leaf agents.** Rejected. Humans messaging units is a first-class flow (the Magazine owner opens an edition by messaging the Director unit); the unit relaying to members is the whole point of a unit.
- **Wait for the v0.2 hat-ergonomics work (`{human_id → user_id}`, #2972, #1470/#1497) before fixing attribution.** Rejected as a sequencing error. The invariant must hold for one hat or many; coupling it to the hat-count cleanup would leave a correctness bug (a human's words misattributed to a unit) open across a release for an orthogonal ergonomics reason.

## Consequences

### Safer / clearer
- A human's directive can never again surface as authored by a unit/agent — the incident-class corruption (#2977) is structurally excluded, not prompt-mitigated.
- Members gain a reliable "who authored this?" signal (`from` vs `from + via`) without the platform minting a second unit identity.
- Audit is complete: relay messages retain both the human author and the relaying unit, mirroring ADR-0062 §4's dual-stamping.

### Costs
- `McpSession` / `ToolCallContext` / `Message` (or its envelope) grow an originating-hat + `via` carrier; the dispatcher must thread the inbound `From` onto the session. Bounded, additive, no back-compat shim required (pre-1.0).
- The "same errand" boundary (§5) is a genuine judgement that needs care in implementation; getting it wrong over-attributes (unit's own message tagged as a human relay) or under-attributes (relay tagged as the unit's own). The explicit-intent rule narrows the blast radius.

### What this implies
- **Implementation is not bundled here** (docs-only PR). Proposed split, to be filed and wired as sub-issues of #3001:
  1. Carry the originating hat on the per-turn MCP session + `ToolCallContext` (dispatcher reads inbound `From`; `McpSession.OriginatingHuman`).
  2. Stamp `from: human:<id>` + `via: unit:<id>` on relayed sends in `MessageDeliveryService` / `MessagingToolHandlers`; render `via` in `InboundEnvelopeBuilder` and the activity envelope; settle the §5 boundary rule.
  3. Prompt update (`PlatformPromptProvider`): document `via` and that a member may trust `from` as authorship.
- **No migration.** Pre-1.0; the envelope/session additions are additive.
- **Hat ergonomics stay in v0.2** (#2972 stale hats, #1470/#1497 humans as members, `{human_id → user_id}` consolidation) — out of scope here and not a blocker.

## Revisit criteria

- If the explicit-intent rule (§5) proves too easy for a runtime to get wrong, revisit toward thread-scoped attribution.
- If members genuinely need to *address* the relaying unit on a relayed message (not just see it), promote the `via` provenance (shape (a)) to the compound `(author, on_behalf_of)` sender (shape (b)).
- If #2878 lands first and collapses `unit:`→`agent:` at the runtime surface, re-confirm that the own-vs-relay signal still reads cleanly when both author and carrier render as `agent:`.
