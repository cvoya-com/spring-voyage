# 0061 — Slack connector v0.1: OSS shape and multi-tenant seams

- **Status:** Accepted (2026-05-26). v0.1 work — implementation tracked in a follow-up issue.
- **Date:** 2026-05-26
- **Related ADRs:** [0030](0030-thread-model.md) — participant-set thread identity (the Slack-side surface inherits this directly); [0045](0045-connector-domain-agnostic-platform.md) — connector contract (extended here for tenant-scoped binding); [0047](0047-platform-user-human-split.md) — `TenantUser` and per-tenant display-identity rows (the Slack-mapped user identity sits on this seam); [0060](0060-participant-set-agent-api-and-structured-envelope.md) — participant-set agent API (the inbound payload from Slack is wrapped in the envelope this ADR specifies); [0062](0062-tenant-user-human-explicit-binding.md) — `Human → TenantUser` FK and `ITenantUserHumanResolver` (Slack `Message.From` is rewritten through this resolver at the connector event-handler boundary; the slug-rule's Hat-to-drop lookup consumes it).
- **Related docs:** [`docs/architecture/connectors.md`](../architecture/connectors.md) (to be updated under implementation), [`docs/concepts/messaging.md`](../concepts/messaging.md) (referenced unchanged).
- **Related code:** `src/Cvoya.Spring.Connectors.Abstractions/IConnectorType.cs` (binding-scope contract extension), `src/Cvoya.Spring.Connector.GitHub/` (reference implementation; Slack connector follows the same project layout under `src/Cvoya.Spring.Connector.Slack/`).
- **Future work:** Portal observation of SV threads is deferred to the portal observation milestone (see follow-up issue filed alongside this ADR).

## Context

Spring Voyage's connector model to date treats the unit as the binding scope: `UnitGitHubConfig` lives on a `(tenant, unit, connector)` row; the GitHub connector wires one App-installation or PAT credential to one unit; webhook payloads fan out to per-unit bindings within a tenant (ADR-0047 §10). That shape composes for connectors where the external resource (a GitHub repo) is naturally a per-unit concern.

Slack does not compose the same way. The natural unit of Slack identity is the **workspace**: one Slack workspace has one bot identity per installed app, and a Slack user interacting with the bot has exactly one DM with it regardless of how many SV units exist on the other side. Per-unit Slack bindings would mean one Slack app (and therefore one bot identity) per unit, forcing the operator to install N Slack apps and the user to track N bot DMs in their sidebar for a single SV tenant. That is the opposite of the "one Slack workspace ↔ one SV tenant" mental model the design discussion converged on.

The design discussion also resolved two adjacent shape questions:

- **SV threads are stable, participant-set-identified** (ADR-0030). A Slack-side container that represents an SV thread should track that thread's lifetime, not the lifetime of a conversational episode.
- **Slack apps install at workspace scope** (no individual-user install primitive exists). The OSS install pattern therefore lands a workspace-scoped app whose bound human is the OAuth installer.

Together those constraints push v0.1 toward a constrained, single-Slack-user, DM-only shape that fits OSS cleanly and leaves the multi-user / multi-tenant generalisation as a layered extension rather than a rewrite. This ADR records the v0.1 shape and the seams that keep the generalisation cheap.

## Decision

### 1. The Slack connector binding is tenant-scoped, not unit-scoped

The Slack binding lives on the **tenant**, not on a unit. There is at most one Slack binding per tenant; that binding addresses one Slack workspace (`team_id`) and holds the bot OAuth credentials, the signing secret, and the OAuth installer's Slack `user_id`. Per-unit and per-agent Slack bindings are explicitly out of scope — there is no `UnitSlackConfig`, no agent-level Slack config, and no per-binding scoping below tenant.

Justification:

- **Slack's native scoping is the workspace.** One Slack app = one bot user. A per-unit binding model would force one Slack app per unit, multiplying operator install steps and bot DMs in the user's sidebar.
- **SV threads cross units.** A thread `{human, unit-A, agent-from-unit-B}` has participants from two units; routing its Slack-side surface to either unit's binding would arbitrarily privilege one. Tenant scope avoids the question.
- **The user's Slack-side identity is per-tenant, not per-unit.** ADR-0047 §1 already established `TenantUser` as per-tenant; `TenantUserConnectorIdentity` (the row holding the Slack `user_id` for that user) is keyed on `(tenant, tenant_user, connector)`. Tenant-scoped binding aligns with that key.

**Contract extension.** `IConnectorType` (ADR-0045) gains a `BindingScope` enum-shaped property whose values are `Unit` (today's default — GitHub, Arxiv, Web Search) and `Tenant` (new — Slack). The generic connector endpoints (`/api/v1/tenant/connectors/...` and the bindings sub-routes) gain a tenant-scoped variant at `/api/v1/tenant/connectors/{slug}/binding` (singular, no unit segment) for `Tenant`-scoped connectors. The binding store grows a `tenant_connector_bindings` table alongside the existing `unit_connector_bindings` table; both are addressed through `IUnitConnectorBindingStore` / a sibling `ITenantConnectorBindingStore` so the resolver path is uniform.

Future connectors that are intrinsically workspace-shaped (calendar integrations, mailbox connectors, future chat connectors) reuse the `Tenant` binding scope. The connector contract carries the decision; no per-connector branching in the platform host.

### 2. OSS v0.1 restrictions

The OSS deployment ships with five restrictions that make the Slack UX coherent and remove edge cases the platform does not yet support.

**2.1 Single bound Slack user.** Exactly one Slack `user_id` is bound, and it is the OAuth installer of the Slack app. That user is the `TenantUser` mapped to the OSS default tenant user (`OssTenantUserIds.Operator`). Other workspace members exist in Slack but are unbound on the SV side.

**2.2 DM-only operation.** The bot operates only in its DM with the bound user. On any `member_joined_channel` event where the joining member is the bot itself, the bot posts a single message ("This Spring Voyage install is bound to one user and only operates in DM with that user. Leaving.") and calls `conversations.leave`. The bot does not subscribe to `message.channels`, `message.groups`, or `app_mention` event types. This keeps the OAuth scope footprint small (decision 6) and makes the "what surfaces does this bot operate on?" answer trivial.

**2.3 No Enterprise Grid / org-level install.** Standard workspace install only. The binding flow inspects the `enterprise.id` field on the `oauth.v2.access` response at install time; if it is non-empty, the workspace is part of an Enterprise Grid and binding is refused with a structured error (`SlackEnterpriseGridUnsupported`). Grid changes the auth model in ways the v0.1 connector does not handle, so explicit refusal is preferable to silent partial-functionality. (We do not also call `team.info` for a second opinion — that would require the `team:read` scope, which the connector otherwise does not need.)

**2.4 Unbound-user refusal.** If a workspace member other than the bound user DMs the bot, the bot replies once with `"This Spring Voyage install is bound to <installer-display-name>. You don't have access."` and ignores subsequent messages from that user. The refusal is observable (audit log), not silent.

**2.5 One workspace per install.** The OSS connector binding pins one `team_id`. Re-binding to a different workspace is supported (the operator can drop and recreate the binding), but the platform does not track multiple workspaces for one OSS install. The Slack app itself may be configured for multi-workspace distribution (decision 7.5) — that is the distribution model, not the binding model.

### 3. SV thread → Slack thread inside the bound user's bot DM

The Slack-side surface for every SV thread that includes the bound user is a Slack thread (`thread_ts` chain) inside the bot's DM with that user. Mechanics:

- **One DM per Slack user.** The bot opens its DM with the bound user via `conversations.open` at bind-completion time. This is the entry point.
- **One Slack thread per SV thread.** The first time the bot needs to surface a message for an SV thread `T`, it posts a parent message in the DM whose text is the slug derived from `T`'s participants (decision 4). The Slack `thread_ts` of that parent message is recorded on the SV thread's connector-binding state (`thread_id ↔ slack_thread_ts`).
- **Subsequent messages on that SV thread** are posted as threaded replies under that parent via `chat.postMessage` with `thread_ts` set. Outbound messages from the user — replies in the Slack thread — are routed back to the SV thread by reverse lookup.
- **Persona overrides for non-bound participants.** Every bot post on behalf of an SV agent, unit, or non-bound SV human is rendered with `chat.postMessage`'s `username` and `icon_url` parameters set to the participant's SV display name and avatar. This requires the `chat:write.customize` OAuth scope.
- **Threads with no bound user are invisible to Slack.** `{agent-1, agent-2}`, `{unit-1, agent-2}`, and similar all-non-human threads have no Slack-side surface in v0.1. They are observable through the portal (the deferred portal observation work).

The agent-facing payload for inbound Slack messages is wrapped in the structured envelope from ADR-0060 — `from` is the bound user's SV human address (with display name), `to` is the SV thread's participant set, `payload` is the Slack message text.

### 4. Slack-thread parent message slug

The parent message of each Slack thread carries a human-readable slug naming the SV-side participants. The rule:

> Concatenate the display names of every SV participant in the thread, separated by `-`, **dropping the one SV human designated as primary for the bound `TenantUser`**, prefixed with `sv-`.

Worked examples (bound `TenantUser` = the OSS operator; primary SV human = "alex"):

| SV thread participant set | Slack-thread parent slug |
|---|---|
| `{human:alex, agent:bob}` | `sv-bob` |
| `{human:alex, agent:bob, unit:research}` | `sv-bob-research` |
| `{human:alex, agent:bob, unit:research, human:morgan}` | `sv-bob-research-morgan` |
| `{human:alex, human:morgan, agent:bob}` | `sv-bob-morgan` |

**Why every non-primary SV human stays in the slug** (including humans that, in OSS, map to the same `TenantUser` as `alex`): the slug must be unique per SV thread, and SV thread identity is the *participant set* (ADR-0030). Collapsing all SV humans mapped to the same `TenantUser` to a single name would lose set uniqueness — `{alex, bob}` and `{alex, morgan, bob}` would render identically. The slug therefore preserves every SV human's name except exactly one: the primary SV human of the bound `TenantUser`.

**Hat-to-drop selection** (revised post-[ADR-0062](0062-tenant-user-human-explicit-binding.md)). The Hat dropped from the slug is the Hat the SV thread is rendered as for the bound `TenantUser`. ADR-0062 § 5 pins this per-thread: replies adopt the Hat the thread came in on; new threads (started via `/sv-thread`) fall back to `T.PrimaryHumanId` (ADR-0062 § 2). The slug-builder consumes `ITenantUserHumanResolver` (ADR-0062 § 3) for both branches — there is no separate Slack-side resolver. In OSS where every `Human` maps to the single operator `TenantUser`, both branches collapse to `T.PrimaryHumanId`, which is set automatically when the operator's first `Human` row binds (ADR-0062 § 2).

Slack thread parent message text is free text and is not subject to Slack's channel-name uniqueness rules; no slug collision concern.

### 5. Slash-command surface

The bot registers three slash commands. All three operate in the bound user's DM only; if invoked elsewhere they reply with the same DM-only message as the unbound-user refusal.

**`/sv-thread`** — opens a Block Kit modal. The modal shows a multi-select populated from the SV directory (agents, units, and SV humans other than the primary), with an optional initial-message field. On submit, the connector:
1. Resolves the SV thread `T` corresponding to `{primary-human, ...selected}` via `IThreadRegistry.GetOrCreateAsync(participants)`.
2. Posts a parent message in the DM with the slug from decision 4.
3. Records `T.thread_id ↔ slack_thread_ts`.
4. If an initial message was supplied, routes it to SV as the user's first message on that thread.

**`/sv-threads`** — opens a modal listing every SV thread the bound user is a participant in that has a Slack-side surface in this workspace. Each row deep-links to the parent message of that Slack thread for navigation.

**`/sv-help`** — posts a usage cheat-sheet in the DM.

Slack autocomplete does not know SV participants; the modal-based selection is the v0.1 surface. A natural-language entry point (`@sv-bot start a thread with bob`) is explicitly out of scope.

### 6. OAuth scopes

The minimum scope set for v0.1, derived from the DM-only restriction:

| Scope | Why |
|---|---|
| `chat:write` | Bot posts in DMs |
| `chat:write.customize` | Persona overrides (`username`, `icon_url`) for SV participants |
| `im:history` | Read DM messages from the bound user |
| `im:write` | Open / write DMs |
| `im:read` | DM metadata (member list confirmation) |
| `users:read` | Resolve `user_id` → display name for the binding |
| `users:read.email` | Map the OAuth installer to an SV `TenantUser` by email (optional; pasteable fallback) |
| `commands` | Slash commands |
| `channels:read` / `groups:read` | Receive `member_joined_channel` so the bot can auto-leave |

`channels:history`, `groups:history`, `mpim:*`, `app_mentions:read`, and any `team:*` scopes are explicitly **not** requested in v0.1. Future generalisations add the ones they need (decision 7).

### 6.1 OAuth credential persistence and resolution order (post-implementation refinement, [issue #2849](https://github.com/cvoya-com/spring-voyage/issues/2849))

§2.5's "each OSS install has its own Slack app" statement specifies the install model but is silent on **where** the four OAuth credentials (`ClientId`, `ClientSecret`, `SigningSecret`, `RedirectUri`) physically live. The runtime ships with three persistence tiers, queried per call in fixed order:

1. **Tenant-scoped secret** at the well-known name (`slack-oauth-client-id`, `slack-oauth-client-secret`, `slack-oauth-signing-secret`, `slack-oauth-redirect-uri`). Written by `spring connector slack install --write-tenant-secrets`.
2. **Platform-scoped secret** at the same name. Written by `spring connector slack install --write-secrets`.
3. **Env-config** bound from `Slack:OAuth:*`. Written by `spring connector slack install --write-env` or a hand-edited `spring.env`.

The chain is per-field, not all-or-nothing: an operator can pin `RedirectUri` in env-config and override `ClientSecret` via a tenant-scoped row without inconsistency. Non-credential fields (`Scopes`, `StateTtl`) come only from env-config — they are install-time / operational tunables, not credentials.

The connector consumes the resolved snapshot through `ISlackOAuthOptionsResolver` (a singleton that opens a scope per call for `ISecretResolver` + `ITenantContext`). This is the same scope-factory pattern `SlackInstallStore` uses; nothing about the v0.1 single-tenant shape changes.

**Relationship to §7.** The tenant-scoped persistence tier is the load-bearing prerequisite for §7.5's multi-workspace-per-tenant generalisation: each tenant's row of Slack credentials lives at tenant scope, so a future multi-tenant install lands additional rows without touching platform config. §7's full generalisation (per-binding credentials, per-binding scoping below tenant) is tracked at [issue #2850](https://github.com/cvoya-com/spring-voyage/issues/2850); §6.1 here only pins the persistence-and-resolution order.

The v0.1 single-tenant shape description in §2.5 stays correct as written — this refinement is about **where** the four credentials live, not about the install model itself.

### 7. Forward-compatibility seams (multi-user per tenant; multi-tenant)

Implementers MUST preserve the following seams in v0.1 so that the multi-user-per-tenant and multi-tenant generalisations land as additive changes, not rewrites.

**7.1 Bound users are a list.** The connector queries `ITenantConnectorBindingStore.GetBoundUsersAsync(tenant_id, "slack")` and gets back a list of `(SlackUserId, TenantUserId)` mappings. In OSS v0.1 the list has length 1. The code that dispatches inbound messages, opens DMs, and resolves "is this Slack user bound?" iterates the list; nothing assumes a singleton. Renaming or collapsing the lookup to a "get the bound user" shape is a regression and must be avoided.

**7.2 DM-vs-channel routing is a function of the participant set.** The routing function takes an SV thread and returns one of `DirectMessage(slack_user_id)`, `PrivateChannel(channel_id)`, or `None` (no Slack-mapped human in the thread). In OSS v0.1 only the first and third branches ever fire because there is never more than one bound human. The branch for `PrivateChannel` is stubbed (`throw NotSupportedException` or equivalent — explicit, not silent) so the future hybrid mode (multi-mapped-human SV threads land in private channels) drops into the same routing function without restructuring callers.

**7.3 Channel auto-leave is gated on a "single-user mode" flag, not hardcoded.** The connector reads `single_user_mode: bool` from its binding-config row. In OSS v0.1 this defaults to `true`; the auto-leave + scope-omission behaviour is conditional on it. Multi-user installs flip the flag to `false` and the connector subscribes to channel events normally. The flag is **not** a runtime override (no per-message check) — it gates which event subscriptions and which scopes the connector requests at install time.

**7.4 Hat-to-drop resolution flows through `ITenantUserHumanResolver`** (revised post-[ADR-0062](0062-tenant-user-human-explicit-binding.md)). The slug-builder (decision 4) calls `ITenantUserHumanResolver` (ADR-0062 § 3) to determine which Hat to drop from a given thread's slug. The resolver's existing hierarchy — per-thread reply Hat → `T.PrimaryHumanId` → 400 `NoBoundHuman` — is the right shape for both OSS (single-Hat collapse) and multi-tenant (multi-Hat-per-TenantUser); the slug-builder does not branch on deployment mode and does not invent a parallel resolver. Multi-tenant generalisations land entirely in ADR-0062's resolver, not here.

**7.5 OAuth install supports multi-workspace distribution from day one.** The Slack app's `oauth.v2.access` flow accepts any workspace install; the platform's OAuth callback resolves the installing workspace's `team_id` to an SV tenant via a `tenant_id ↔ team_id` mapping table. In OSS v0.1 the mapping table has one row (default tenant ↔ the operator's workspace), and the callback refuses installs from any other workspace with a structured error. In multi-tenant SaaS the mapping table grows one row per installed workspace, populated either by an admin association step in the SV portal or by a deterministic mapping (e.g., the installing user's SV tenant). The OAuth flow and the callback handler are the same code; only the mapping table behaviour differs.

**7.6 Slack Enterprise Grid org install is deferred but the seam is named.** `IConnectorType.SlackBindingMode` enum has values `Workspace` (v0.1) and `Org` (future). The binding row carries a `mode` column. In v0.1 the connector refuses `Org` mode at install (`SlackEnterpriseGridUnsupported`). The future Grid implementation adds the `Org`-mode install path without touching `Workspace`-mode code. SaaS tenants on Grid orgs (the user's "different SV tenants → different Slack orgs" framing) install in `Org` mode and bind one Grid org per tenant.

**7.7 Tenant-scoped binding store is generic, not Slack-specific.** The `tenant_connector_bindings` table introduced by decision 1 stores opaque JSON per `(tenant, connector_slug)` exactly like the existing `unit_connector_bindings` table. The Slack connector is the first consumer; future tenant-scoped connectors (workspace-wide mailbox, calendar) reuse the same table without per-connector storage code.

**7.8 No DM affinity assumptions in routing.** The routing function (7.2) returns a Slack-side container reference; downstream code does not assume "container = DM" anywhere. When the future hybrid mode introduces `PrivateChannel` returns, the only new code is the private-channel creation / lookup; the posting and reply-routing paths read the container reference uniformly.

Implementers are not required to *implement* the multi-user or multi-tenant code paths in v0.1. They are required to keep the v0.1 implementation behind the right function signatures so adding those paths is a localised diff.

## Alternatives considered

- **Per-unit Slack binding (mirror GitHub's shape).** Rejected — would mean one Slack app per unit, multiplying operator install steps and forcing users to track N bot DMs. Slack's "one app = one bot" rule makes the per-unit shape work against the platform's grain, not with it.

- **Channel-per-thread instead of Slack-thread-per-thread.** Considered for visual separation. Rejected: Slack channel names must be unique workspace-wide and require a per-thread disambiguator slug; the resulting `sv-thread-<hash>` slug is opaque to users, the sidebar fills with channels, and Slack's native threading primitive (built precisely for "a sub-conversation under a parent context") is bypassed. Slack threads inside the bot DM use the right primitive.

- **MPIM (multi-person IM) for multi-bound-human threads in the hybrid future.** Considered. Rejected as the v0.1+ recommendation: MPIMs lack bookmarks, canvases, pinned-message UX, and `/commands`, and cap at ~8 humans + the bot. The future hybrid mode (decision 7.2) uses private channels for ≥2-bound-human threads instead, which provides those affordances and scales further. MPIMs may still appear in implementation if a tenant explicitly opts for them, but they are not the default.

- **Mix DM (for 1:1) + private channel (for multi-human) in OSS.** Considered. Rejected for OSS specifically: OSS has exactly one bound user, so the multi-human branch never fires; including it in OSS adds code paths without exercising them. The seam is preserved (decision 7.2) so the branch lands cleanly when needed; OSS does not pay for it today.

- **Allow channel invites in OSS for "shared visibility" of the bot.** Initially proposed and then withdrawn during design. Rejected because in OSS the only Slack-mapped SV user is the operator, so a channel containing the bot adds no visibility for anyone else; meanwhile silent presence in channel member lists is confusing UX. Auto-leave with an explanation message is the clearer signal.

- **Drop slugs from Slack-thread parent messages; use opaque thread ids.** Considered for simplicity. Rejected: the slug is the primary navigation affordance for the user in the DM — it is how they distinguish their threads when scrolling the DM or searching. Opaque ids cost no implementation time but materially worsen the UX.

- **Defer the multi-tenant seams to a follow-up ADR.** Rejected at the user's explicit request: v0.1 implementers are to design for multi-user-per-tenant and multi-tenant from day one, even though v0.1 only ships the single-user / single-tenant configuration. Recording the seams in this ADR is the durable form of that instruction.

## Consequences

### Gains

- **One Slack workspace ↔ one SV tenant** is the natural mental model and the install flow matches it. Users see one bot DM, one set of slash commands, one place to navigate their SV threads.
- **The per-binding shape generalises.** Tenant-scoped bindings land as a reusable platform feature, not a Slack-specific hack. Future workspace-shaped connectors (mailbox, calendar, future chat) reuse the same row, store, and endpoints.
- **The OSS UX is honest about its scope.** Unbound-user refusal, auto-leave from channels, Enterprise Grid refusal — all explicit. There are no quiet "this kind of works but…" edges.
- **The multi-user / multi-tenant path is incremental.** Per decision 7, the v0.1 code already wears the function shapes the future code needs; the future PRs add behaviour to those functions rather than restructuring callers.
- **The agent-facing contract is unchanged.** Inbound Slack messages land in the same envelope as inbound webhooks from other connectors (ADR-0060). Agents do not see Slack-specific shapes.

### Costs

- **Two binding-scope tables instead of one.** `tenant_connector_bindings` lands alongside `unit_connector_bindings`. The resolver path is uniform but the storage layer has one more table.
- **One more `IConnectorType` contract field.** `BindingScope` (and `SlackBindingMode` for the Grid seam) extend ADR-0045's connector contract. Existing connectors default to `Unit` scope; the change is additive but every `IConnectorType` implementation acknowledges the field.
- **Slack-thread state on SV threads.** Each SV thread now carries an optional `slack_thread_ts` per bound user (in OSS, a single optional value). The `thread_connector_state` table or equivalent grows one column for the connector to record the mapping; the lookup is hot-path on every inbound and outbound message.
- **Implementers carry the v0.2 seams from v0.1.** Decision 7's signatures and flags are extra surface in v0.1 that exists only to keep v0.2 cheap. The cost is small but real (more interfaces to keep coherent during refactors).
- **Slack distribution model needs a public Slack app for SaaS.** OSS uses per-operator-created Slack apps. The future SaaS shape (decision 7.5) needs a cvoya-com-published Slack app for the App Directory. That app's manifest, review, and lifecycle are platform-team responsibilities; they do not block v0.1 but they are work that v0.2 carries.

## Revisit criteria

Reopen this decision when any of the following holds:

- **Multi-tenant SaaS Slack distribution lands.** Decision 7.5's mapping-table behaviour gets concrete admin UX; the OAuth callback grows the "which SV tenant does this install land on?" resolution path. The mapping shape may need refinement based on real customer flows.
- **Multi-user per tenant lands.** Decision 7.1's "bound users is a list" goes from length-1 to length-N. Decision 7.2's `PrivateChannel` branch ships. The hybrid model's private-channel naming, lifecycle, and member-management need their own ADR.
- **Enterprise Grid customer demand.** Decision 7.6's `Org` mode goes from "refused" to "supported." The Grid OAuth flow, the Grid → tenant mapping, and the per-workspace-inside-Grid behaviour need design work.
- **A connector other than Slack needs `Tenant`-scoped binding.** Decision 1's generic seam gets a second consumer. Confirm the contract field is the right shape (e.g., does it need a tenant-and-unit hybrid scope for connectors with both concerns?).
- **A real use case for SV threads with no bound user surfacing in Slack emerges.** Today such threads are portal-only. A future "post a digest of agent-only threads to a Slack channel" use case would reopen the DM-only restriction.
