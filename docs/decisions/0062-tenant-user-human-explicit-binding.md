# 0062 — Explicit `Human → TenantUser` FK; `Message.From` is always routable

- **Status:** Accepted (2026-05-26). v0.1 work — implementation tracked in a follow-up issue.
- **Date:** 2026-05-26
- **Amends:** [ADR-0047 § 7](0047-platform-user-human-split.md) — brings the explicit `Human → TenantUser` mapping forward from the umbrella's v0.2 deferral (OUT2) to v0.1, and pins the data shape (FK on `humans`, NOT NULL with a default-resolver service). The mapping is no longer a derived projection.
- **Adjacent ADRs:** [0036 — Single-identity model](0036-single-identity-model.md) (actor-kind enumeration; `tenant-user` remains an actor kind but is removed from the routable-recipient set for domain messages), [0046 — Unified members grammar](0046-unified-members-grammar.md) (the `Human` member kind whose per-unit display name moves to the membership row).
- **Related code:** `src/Cvoya.Spring.Host.Api/Auth/AuthenticatedCallerAccessor.cs` (still returns `tenant-user://` — unchanged), `src/Cvoya.Spring.Host.Api/Endpoints/MessageEndpoints.cs` (the message-construction site that rewrites `From` to `human://` per § 3), `src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs` (`EnsureRoutableRecipientScheme`; the agent-facing tool boundary that already rejects `tenant-user:` and continues to), `src/Cvoya.Spring.Dapr/Observability/OssInboxIdentityResolver.cs` (shrinks to a reverse-FK query), `src/Cvoya.Spring.Dapr/Data/Entities/HumanEntity.cs` (gains `TenantUserId`), `src/Cvoya.Spring.Dapr/Data/Entities/TenantUserEntity.cs` (gains `PrimaryHumanId`).
- **Related docs:** [`docs/concepts/humans.md`](../concepts/humans.md) (to be updated under implementation), [`docs/concepts/messaging.md`](../concepts/messaging.md) (the "who is the sender" paragraph re-anchors on §3 here).

## Context

The TenantUser / Human split landed in [ADR-0047](0047-platform-user-human-split.md) with one piece deferred: § 7 deferred the explicit `Human → TenantUser` mapping table to v0.2 (OUT2 in the umbrella), keeping the OSS-default rule "every Human in the tenant maps to the single operator" as a derived projection in `OssInboxIdentityResolver`. The deferral was reasonable when the only consumer was the inbox-read path — a derived projection is enough when the question is "which inboxes do I look at?"

Two things forced the question back open:

1. **A runtime incident.** A unit's first turn produced two `ToolResult` warnings in the activity log (unit `912e57ba5efb4b1db715c36614c6ef9f`, correlation `aae60a8f2c484e94955f8132536f8718`): `sv.directory.lookup` rejected the address with "No agent, unit, or tenant in scope matches uuid 5c4c8e29…", followed by `sv.messaging.send` rejecting the same address with `UnroutableTarget`. The trigger was an inbound `Message.From = tenant-user://5c4c8e29…` (the OSS operator's `TenantUser` address per ADR-0047 §1) being surfaced to the LLM as a candidate reply target. `EnsureRoutableRecipientScheme` (`src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs:219`) correctly rejects non-routable schemes, and `sv.directory.lookup` has no `tenant-user` resolver — both behaviours are deliberate, but they leave the agent looking at a `From` it cannot use. The agent eventually recovered (it found the routable `human://` target on its own) but burned a turn.

2. **A portal manifestation of the same scheme leak.** [#2801](https://github.com/cvoya-com/spring-voyage/issues/2801) / [PR #2803](https://github.com/cvoya-com/spring-voyage/pull/2803) (merged) patched the portal's `roleFromEvent` to map `tenant-user:` → human-bubble rendering. That fix is tactical — the right strategic answer is that domain messages should not carry `tenant-user://` as `From` in the first place, so the portal does not have to recognise the scheme on the read side.

Both manifestations point at the same root: `Message.From` and the auth principal are different concerns. The auth principal (`tenant-user`) survives unchanged on the wire as the authenticated caller for API requests, audit, and permission checks. The message-domain `From` field needs to be a routable address (the routable kinds are `agent` / `unit` / `human` per `EnsureRoutableRecipientScheme`) so every downstream consumer — agent-facing tools, the directory, the routing layer, the portal render path — can treat `From` uniformly.

Bringing the explicit FK forward also unlocks the "wearing different hats" UX the v0.1 inbox is converging on: a single `TenantUser` can be bound to multiple `Human` rows across (and within) units, each with its own per-unit display name; the inbox needs to render which hat received each item and let the operator reply as that hat.

## Decision

### 1. `humans.tenant_user_id` is a NOT NULL FK with a deployment-default resolver

The `HumanEntity` row gains a NOT NULL `TenantUserId` column referencing `tenant_users.id`. The cardinality is many `Human` rows to one `TenantUser` (a `TenantUser` is bound to N `Human` slots — different hats — but each `Human` is filled by exactly one `TenantUser`).

NOT NULL over nullable. The "placeholder Human waiting for a binding" use case is handled by **always stamping a deployment-default `TenantUser` at insert time** — never by leaving the column NULL. Nullability would force every consumer (routing, inbox, directory, permission checks) to branch on "is this Human bound yet?" and the OSS-default rule (always operator) would have to be re-implemented at every consumer. NOT NULL pushes the one defaulting decision into a single service:

```csharp
public interface ITenantUserDefaultResolver
{
    Task<Guid> ResolveDefaultAsync(CancellationToken cancellationToken = default);
}
```

The OSS implementation always returns `OssTenantUserIds.Operator`. The cloud overlay returns the calling `TenantUser` from `IAuthenticatedCallerAccessor` (with explicit `--as` / install-flow overrides taking precedence at the call site, not in the resolver). Every Human-insert path — package install, CLI `spring unit member add human`, portal member-add, test seeders — calls the resolver unless an explicit binding is supplied.

This mirrors `ITenantContext`'s shape: one resolver, called once at the right moment, deployment-overrideable via DI.

### 2. `tenant_users.primary_human_id` pins the default "hat" for outbound

The `TenantUserEntity` row gains a nullable `PrimaryHumanId` FK. It pins which of the user's bound `Human` rows is the default `From` for **new outbound messages** (inbox composer's "new message" action, CLI `spring message send` without `--as`). Within an existing thread, the reply composer pins the hat the thread came in on regardless of `PrimaryHumanId` (§ 5).

The column is nullable to allow a freshly seeded `TenantUser` with no Humans yet, and is set automatically when the user's first Human binding is created. The user can repin via the portal's identity settings or `spring user identity set-primary <human-ref>`.

### 3. `Message.From` is always a routable scheme; the rewrite happens at the API boundary

`Message.From` MUST carry one of the routable schemes (`agent` / `unit` / `human`). `tenant-user://` is **not** a valid value for `Message.From` on a domain message.

`IAuthenticatedCallerAccessor` is unchanged — it still returns `tenant-user://<id>` because that is the auth principal and remains the right shape for audit, permission checks, and OpenAPI request-context wiring. The rewrite to `human://` happens in **`MessageEndpoints`** (and the equivalent CLI-side message-send path), at the construction site for the outbound `Message`:

```text
caller (tenant-user://T) → ITenantUserHumanResolver.PickFromAsync(T, request.from?) → human://H → Message.From
```

`ITenantUserHumanResolver.PickFromAsync` returns:

- The Human named by an explicit `from` field on the request (if supplied), after validating that it is one of T's bound Humans.
- Otherwise the Human bound to the thread on the inbound side (reply default — see § 5).
- Otherwise `T.PrimaryHumanId`.
- Otherwise a 400 with the structured code `NoBoundHuman`, hinting at the bind / claim flow.

The "or 400" branch is unreachable in OSS (the operator always has at least one bound Human via the default resolver in § 1) but is the correct error for a cloud `TenantUser` who has not yet been bound to any Human.

**The wire shape of `Message.From` is unchanged for downstream consumers.** Routing, the directory, the agent-facing tool surface (`sv.messaging.send`, `sv.directory.lookup`), and the portal's `roleFromEvent` continue to see `human://<id>` on every message, exactly as before #2768. The activity-log incident in Context disappears as a structural consequence: the agent never sees `tenant-user://` in a `From` field, so it never tries to send to or look up that scheme.

### 4. Audit and activity capture **both** identities

The strategic risk of rewriting `From` is losing the auth principal in the audit trail ("which TenantUser actually sent this — vs which Human were they speaking as?"). The activity event for an outbound message MUST record:

- `from.address` — the routable `human://<H>` (matches `Message.From`).
- `acting_tenant_user_id` — the `tenant-user://<T>` principal that authenticated the API request.

The cloud overlay's permission system already keys on `acting_tenant_user_id` (per `PermissionService` and ADR-0047 §1); preserving it in the activity stream lets that permission decision be reconstructible from observation alone. The OSS render strips `acting_tenant_user_id` from the default activity view (it always equals the operator) but keeps it in the JSON envelope for symmetry with cloud.

The same dual-stamping applies to thread-level annotations: a thread's "sent by" rendering is the human display name, but the audit envelope carries the `TenantUser` that drove the send.

### 5. Inbox is rendered per Hat; reply pins the Hat the thread came in on

The inbox list-view labels each item with the Human (Hat) that received the message — `As Bob (designer in Magazine)` — without conflating items received as different Hats. The current "fan-in inbox" rendering (every Human in the tenant maps to the operator, items pile into one list) is preserved as the default OSS view, but the per-Hat lane MUST be visible (a column, a chip, or a filterable badge — exact treatment is a UX call by design-engineer in the implementation).

When the operator opens a thread, the reply composer's `From` is **pinned** to the Hat that received the inbound message for the duration of that thread. The user can override by switching Hat via the from-selector, but the default is "reply as the Hat you were addressed as." This is the rule that makes the model earn its keep: replies do not silently change identity mid-thread.

For **new outbound** (not a reply — composer launched from a unit/agent page, from `spring message send`, from the engagement-list compose action), the `From` defaults to `T.PrimaryHumanId` (§ 2) with the same from-selector available.

### 6. CLI: every message-send and member-add command accepts `--as`

The CLI gains two parallel `--as` flags, distinct surfaces with distinct meanings:

- **`spring message send --as <human-ref>`**: explicit `From` Hat for an outbound message. Default = `PrimaryHumanId` resolution per § 3.
- **`spring unit member add human --as <tenant-user-ref>`** (and the equivalent on `package install`): explicit binding for a newly created Human row. Default = the default resolver in § 1.

`<human-ref>` accepts the Human id (dashed or no-dash) or the display name when unambiguous within the calling user's bound set. `<tenant-user-ref>` accepts the TenantUser id, the OAuth subject, or the literal `me` (the calling caller).

CLI parity with the portal is a hard requirement. If the portal surfaces a from-selector on inbox / engagement / unit-messaging-tab, the CLI surfaces `--as` on the equivalent verb. The reverse also holds.

### 7. `OssInboxIdentityResolver` shrinks to a reverse-FK query

With the explicit FK landed, `OssInboxIdentityResolver.ResolveHumanIdsAsync` collapses from "return every Human in the tenant" to:

```csharp
return await db.Humans
    .AsNoTracking()
    .Where(h => h.TenantUserId == callerTenantUserId)
    .Select(h => h.Id)
    .ToListAsync(ct);
```

The OSS and cloud impls become structurally identical — the only difference is which `TenantUser.Id` arrives in the caller address. The cloud overlay's separate registration can be **deleted**; one `InboxIdentityResolver` covers both deployments. ADR-0047 § 7's "cloud overlay walks the explicit mapping table" prediction lands as "cloud and OSS walk the same FK."

The dedicated OSS resolver class is removed in the same implementation pass. Per the v0.1 aggressive-cleanup rule, no shim or transitional path is kept.

### 8. The agent-facing tool surface is unchanged

`EnsureRoutableRecipientScheme` (`src/Cvoya.Spring.Dapr/Skills/SvMessagingSkillRegistry.cs:219`) continues to reject `tenant-user://` as a non-routable recipient. The error message keeps its current wording — the LLM never sees `tenant-user://` as `Message.From` after this ADR lands, so the "agent tries to reply to it" failure mode is structurally extinct rather than caught at the tool boundary. The boundary check stays as defence-in-depth.

`sv.directory.lookup` is unchanged. `tenant-user://` is not in scope for directory resolution.

### 9. Schema migration: forward-only, no row migration

Per the v0.1 freezing-release rule (ADR-0036 § "Schema reset"; ADR-0047 § 8):

- `humans` table gains `tenant_user_id uuid NOT NULL` with FK to `tenant_users.id`.
- `tenant_users` table gains `primary_human_id uuid NULL` with FK to `humans.id`.
- The OSS-default `DefaultTenantUserSeedProvider` is extended to backfill `tenant_user_id` on every existing Human row with `OssTenantUserIds.Operator`, and to set the `TenantUser`'s `primary_human_id` to the first such Human if any exist.
- Local development databases reset on the v0.1 deploy; no separate row-migration script.

The forward-only stance follows the standing v0.1 policy. The migration is a `Migrations/` addition in the same package layout as recent migrations.

### 10. `tenant-user` actor kind is retained; `Address.TenantUserScheme` stays

The `tenant-user` actor kind enumerated by ADR-0036 § 1 (extended via ADR-0047 § 1) is retained. `Address.TenantUserScheme` remains in `Cvoya.Spring.Core/Messaging/Address.cs`. The auth path, the OpenAPI request-context shape, and `PermissionService`'s caller resolution still depend on the scheme.

What changes is the **expected position** of `tenant-user://` in a domain message: **never as `Message.From`, never as a routing target, never as a directory lookup input.** It appears in the audit envelope (`acting_tenant_user_id`, § 4), in API request-context headers, and in permission decisions, and nowhere else on the message-domain surface.

The `IConnectorType.UserConfigSchema` surface (ADR-0047 § 4) and the `/api/v1/tenant/users/{tenantUserId}/identities` routes (ADR-0047 § 14) are unchanged — those are display-identity surfaces and continue to key on `TenantUser`.

## Consequences

### Gains

- **The activity-log incident becomes structurally impossible.** Agents never see `tenant-user://` in `Message.From`, so the lookup-then-send dance the LLM tried at 22:32 / 22:33 has nothing to land on. The fix is in the API boundary, not in agent-side hint-text or tool-error wording.
- **The portal `roleFromEvent` patch becomes load-bearing only as defence-in-depth.** PR #2803 keeps working but is no longer the front line — the front line is that domain messages stop carrying the scheme the patch was learning to recognise.
- **The "wearing different hats" model lands in v0.1.** Multi-Human bindings per TenantUser get the structural support (FK + primary + per-thread-reply pinning + per-Hat inbox lane) the model needs to be coherent — without forcing a global "one Human per Hat collapse" rule in OSS.
- **Cloud and OSS converge on one inbox resolver.** The cloud overlay's separate `IInboxIdentityResolver` registration goes away; both deployments walk the same FK query. ADR-0047 § 7's "DI seam for cloud override" prediction collapses to "no override needed."
- **Audit trail is more, not less, expressive.** Today's wire identity is the auth principal; after this ADR the wire identity is the speaking-as Hat **plus** the auth principal in the activity envelope. The cloud permission-decision audit gets richer, OSS's stays correct.
- **CLI / portal parity on the from-selector.** Operators reading either the portal or the CLI see the same Hat semantics and the same default-overriding flag.

### Costs

- **Schema migration during v0.1.** A NOT NULL FK with a backfill from the seed provider lands in the same migration; local dev DBs reset per the standing v0.1 rule. No production cloud is running on this branch yet, so there is no live-data migration cost.
- **Every message-construction site must call the from-resolver.** `MessageEndpoints`, CLI `spring message send`, the engagement-compose path, and any other API call that lands a `Message.From` re-thread through one new service. Audit pass identifies every site (the implementation issue tracks it).
- **The `OssInboxIdentityResolver` and its DI registration are deleted.** This is a behaviour-preserving cleanup but the `IInboxIdentityResolver` consumers must continue to compile against the surviving (non-OSS-prefixed) implementation; the implementation issue tracks the rename or the merge into a single resolver.
- **From-selector UX work on three portal surfaces.** Inbox, engagement, unit/agent messaging tab. The per-Hat-lane rendering on the inbox is a separate UX call (design-engineer owns the exact treatment); for v0.1 a visible Hat chip per inbox row is the minimum.
- **Operator-facing concept work.** "Hats" is internal vocabulary; the operator-facing docs (`docs/concepts/humans.md`, the inbox onboarding tour if any) need to explain that one operator can be multiple Humans across units and that messages are received and sent as Humans.

### Alternatives considered

- **Keep the derived projection; just rewrite `Message.From` at the API boundary using the OSS rule.** Defers the FK to v0.2 as ADR-0047 § 7 intended. Rejected: the from-selector and per-Hat inbox lane (§ 5) need to know which Humans a `TenantUser` is bound to in order to populate the selector — that's the same query the explicit FK answers. Synthesising the bound set from the OSS "all Humans" rule works in OSS but does not generalise to cloud, and cloud has to ship the FK eventually. Bringing it forward now is one migration instead of two, and unblocks the v0.1 inbox UX.
- **Collapse `TenantUser` and `Human` into a single entity.** Discussed in the design conversation that produced this ADR. Rejected: it would extinguish the "wearing different hats" framing (one TenantUser bound to many Humans is the load-bearing case), force the OSS-only "one Human per package collapse" restriction (which would in turn break the inbox-per-Hat rendering the model is converging on), and forfeit the PlatformUser → TenantUser → Human layering option for future cross-tenant identity. The split that ADR-0047 § 1 landed earns its keep once the FK lands and the from-selector surfaces it.
- **Make `tenant-user://` routable.** Add an actor resolver for `tenant-user` scheme so `sv.directory.lookup` and `sv.messaging.send` work against it. Rejected: it gives every consumer two ways to address the same person (the `TenantUser` and the bound `Human`), guaranteeing inconsistency at the directory and the per-Hat inbox seams. Non-routability of `tenant-user://` is a useful invariant; the fix is to remove the scheme from `Message.From`, not to relax its routability.
- **Per-unit Human row, no global Human entity.** Each unit owns its own Human row; in OSS they all reference the same logical identity but exist as separate rows. Rejected: it duplicates per-Human attributes (`tenant_user_id`, connector identity references, the eventual permission grants in cloud) across rows that must stay synchronised, and the "known as X in this unit" rename is naturally a property of the unit-membership relation, not of the Human entity. The single-Human-row + per-membership-display-name shape (effectively decision § 5's premise) is cleaner.
- **Auto-rewrite `Message.From` in the runtime layer (post-construction) instead of the API boundary.** Rejected: the rewrite needs the auth principal and the operator-selected `from` field, both of which are only available at the API boundary. Doing it in the runtime layer would require threading the selection through the message envelope and re-deriving the auth principal, which is exactly the conflation this ADR is removing.
- **Use `Message.OnBehalfOf` as a separate field.** Add a sibling field to `Message` so `From` stays the auth principal and `OnBehalfOf` carries the Hat. Rejected: the wire-level invariant we actually want is "`From` is the routable identity the recipient sees and replies to." Adding a second field doubles the surface every consumer reasons about (which one renders in the bubble? which one routes? which one shows in the directory?) and does not solve the agent-facing-tool failure mode — the agent would still see two addresses on the inbound message and have to pick. One routable `From` + audit-side `acting_tenant_user_id` keeps the wire shape simple and pushes the second field to the place it actually matters.

## Revisit criteria

Reopen this decision when any of the following holds:

- **PlatformUser lands.** A platform-wide principal that aggregates `TenantUser` rows across tenants would extend the chain to `PlatformUser → TenantUser → Human`; the audit envelope's `acting_tenant_user_id` becomes one layer of two, and the from-resolver's "explicit override accepts `<tenant-user-ref>`" surface may need a `<platform-user-ref>` analogue.
- **A `TenantUser` with zero bound Humans becomes a routine state in cloud.** Today the OSS default resolver guarantees at least one bound Human; cloud may legitimately have signed-in TenantUsers who have not yet been bound to any Human (mid-invitation, mid-onboarding). The `NoBoundHuman` 400 in § 3 is the correct interim shape; if the state becomes common, the message-construction path may grow a "default invisible Hat" auto-bind rather than failing the send.
- **`PrimaryHumanId` becomes inadequate for the new-outbound default.** If the "one default Hat per TenantUser" rule produces noticeably wrong defaults (e.g., users routinely send the wrong Hat from the engagement-list composer), the default-resolution rule in § 3 widens to consider context (the engagement's unit → pick the Hat that is a member of that unit, fall back to primary).
- **A non-human, non-agent principal needs to send messages.** A service-account or external-system principal that is neither a `Human` nor an `Agent` would either need an actor-kind addition (per ADR-0036 § 1) or one of the existing routable kinds extended to cover it. Today the answer is "model it as an agent;" if that strains, the routable-recipient set is revisited.
