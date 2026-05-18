# 0047 — Platform-user / human split; connector identity on the platform user

- **Status:** Proposed — every authenticated principal of Spring Voyage is a `PlatformUser` (new actor kind), distinct from the `Human` configuration entities that populate unit member rows. Connector-side identity (GitHub login, PAT, Slack handle) is owned by the `PlatformUser`, not by the unit binding and not by every `Human`. The `HumanConnectorIdentity` table is renamed and rekeyed onto `PlatformUserConnectorIdentity`. `UnitGitHubConfig` loses `Owner` and `AppInstallationId` — the binding addresses a `(tenant, owner, repo)` triple only, and outbound auth is resolved at use-time through a deterministic chain (App-installation → platform-user PAT → fail). PAT storage uses ADR-0003's tenant secret store; the user-config row holds a secret name, never the token value. In OSS, every `Human` defaults to the operator's well-known `PlatformUser`, whose id is a deterministic v5 UUID pinned as a `const string` on a new `OssPlatformUserIds` class (same shape as `OssTenantIds.Default`). The CLI verb namespace renames in v0.1 from `spring human identity {set,list,remove}` to `spring user identity {set,list,remove}` with no shim. OAuth-issued PAT acquisition is in scope for v0.1 and wires the existing `Auth/OAuth/` scaffolding into the new user-config surface; manual PAT paste remains as the alternate path. The connector contract (`IConnectorType`) is extended so a connector contributes both unit-binding config schema (today) and user-config schema (new).
- **Date:** 2026-05-18
- **Owner:** @savasp (umbrella).
- **Closes:** [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487)
- **Tracks:** [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487) (umbrella; sub-issues filed against this umbrella after this ADR lands per the plan-of-record under [`docs/plan/v0.1/platform-user-split.md`](../plan/v0.1/platform-user-split.md)).
- **Amends:** [ADR-0036 — Single-identity model](0036-single-identity-model.md) — extends the enumeration of actor kinds in §1 to include `PlatformUser`. The amendment line on ADR-0036 itself lands with the implementation PR that introduces the new actor kind, not this ADR. [ADR-0034 — Spring Voyage OSS dogfooding unit](0034-oss-dogfooding-unit.md) — §§ 4 and 5 are obsoleted: the unit's GitHub binding no longer collects `installation_id` at template-apply time, and the identity boundary is the operator's `PlatformUser` not the unit binding. The amendment line on ADR-0034 itself lands with the implementation PR that rewires the OSS package.
- **Related code:** `src/Cvoya.Spring.Connector.GitHub/UnitGitHubConfig.cs` (record losing `Owner` and `AppInstallationId`), `src/Cvoya.Spring.Dapr/Data/Entities/HumanConnectorIdentityEntity.cs` (table being renamed onto `PlatformUserConnectorIdentity`), `src/Cvoya.Spring.Host.Api/Endpoints/HumanIdentityEndpoints.cs` (endpoints being relocated under `/api/v1/tenant/users/...`), `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/` (existing OAuth scaffolding to wire), `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs` (prose pattern mirrored for the new `OssPlatformUserIds.Operator`).
- **Related docs:** [`docs/plan/v0.1/platform-user-split.md`](../plan/v0.1/platform-user-split.md) (execution plan-of-record), [`docs/concepts/humans.md`](../concepts/humans.md) (updated under the plan), [`docs/concepts/tenants.md`](../concepts/tenants.md) (updated under the plan), [`docs/architecture/identifiers.md`](../architecture/identifiers.md) (updated under the plan).
- **Related ADRs:** [0003 — Secret inheritance Unit → Tenant](0003-secret-inheritance-unit-to-tenant.md) (governs how the PAT secret is read), [0036 — Single-identity model](0036-single-identity-model.md) (Guid identity model amended here), [0034 — OSS dogfooding unit](0034-oss-dogfooding-unit.md) (§§ 4–5 obsoleted here), [0045 — Connector-domain-agnostic platform](0045-connector-domain-agnostic-platform.md) (connector contract extended here to contribute user-config schemas), [0046 — Unified members grammar](0046-unified-members-grammar.md) (the `Human` member kind whose connector identities relocate here).

## Context

Spring Voyage's principal model conflated three orthogonal concerns and missed a fourth entirely, surfaced through a localised webhook-routing bug in [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487):

1. **Repo identity on the binding.** `UnitGitHubConfig` stored `Owner` and `Repo` as if both were addressing columns. Only the qualified `owner/repo` pair is the binding's addressing concern; `Owner` standing alone is half of a name, not an identity.
2. **Calling identity on the binding.** `UnitGitHubConfig.AppInstallationId` told the connector *as which GitHub App installation* SV writes to this repo. This is a property of the calling principal, not of the bound repo — and in the model the connector already auto-resolves `installation_id` from `(owner, repo)` at use-time when the App is installed.
3. **Connector-side user identity on `Human`.** ADR-0046 landed `HumanConnectorIdentity` keyed on `(tenant, human, connector, connector_user_id)`. Every `Human` row that participates in a unit could carry a GitHub login. But "humans" in OSS are configuration entities introduced by a package, not authenticated principals; they default to the operator and inherit the operator's identities. Keeping connector identity on the `Human` row meant either (a) duplicating the operator's GitHub login onto every `Human` in every installed package or (b) accepting that "this human's GitHub login" is meaningless for the OSS default. Neither shape composed.
4. **The missing concept — a platform user.** The authenticated principal *of Spring Voyage itself* — the operator in OSS, tenant users in cloud — had no first-class home in the model. Connector-side user identity (your GitHub login, your PAT, your Slack handle) belongs on the principal, not on every `Human` row that names them.

The trigger for the redesign was webhook routing. `gh webhook forward` delivers repo-shaped payloads without an `installation_id`, so `ResolveDestinationAsync` dropped the event. The localised fix would have been a `(owner, repo)` fallback on the matcher; the architectural fix is to make `(owner, repo)` *the* binding key by removing `installation_id` from the row altogether and storing auth on the principal. With that move, the matcher's two cases collapse to one, and "use case 1" of the original #2487 (PAT against a repo without the SV App installed) becomes a free consequence of the model rather than an extra branch.

The same shape will repeat for every future connector. Slack: the operator's Slack handle and Slack OAuth token belong on the operator, not on every `Human` invited to a unit. Linear: same story. Codifying the platform-user concept now makes adding the next connector additive — its user-config schema slots into a uniform surface — rather than a re-litigation of where credentials live.

ADR-0036 (§§ 1, 8) already established the Guid-keyed identity model and the deterministic-v5-UUID pattern for the OSS default tenant id. This ADR is the next layer above: the principal kind that complements the tenant, and the storage / resolution rules for connector-side identities on that principal.

## Decision

### 1. `PlatformUser` is a new actor kind

A `PlatformUser` is an authenticated principal of Spring Voyage. The OSS deployment ships with exactly one — the operator. The cloud deployment carries many per tenant. The actor-kind enumeration that ADR-0036 §1 pinned (unit, agent, human, connector, tenant) is extended in the implementation PR to include `platform-user`; every property ADR-0036 attaches to actor kinds (Guid identity, `display_name` is presentation-only, `display_name` cannot parse as a Guid, membership graph is the addressing fabric) applies unchanged.

**Name choice — `PlatformUser` over `TenantUser` or `Principal`.** Three candidate names were considered:

- **`PlatformUser`** — chosen. The name reflects what the entity is: a user *of the platform*. The OSS / cloud distinction is symmetric (cloud → many platform users per tenant, OSS → one), the term parses cleanly against the existing "platform" framing (the platform is the SV deployment itself), and there is no clash with the unit-membership `Human` vocabulary that ADR-0046 just settled.
- **`TenantUser`** — rejected. Implies tenancy is the differentiator, but tenancy already lives on every actor via `ITenantScopedEntity`; the name would suggest a tenant-scoped CRUD surface separate from "the user," which is the wrong split. In OSS the only tenant is the OSS default, and "TenantUser" then degenerates into "platform user" anyway.
- **`Principal`** — rejected. Too generic. The model already uses "actor" as the umbrella term (per ADR-0036 §1's enumeration), and "principal" in software-security idiom can mean any authenticated subject (a service account, an API key, a header-borne claim). `PlatformUser` names a *kind* of principal; collapsing it onto the umbrella term loses the discrimination.

The argument against `PlatformUser` is that the word "platform" already does work in the codebase (`SecretScope.Platform`, "platform-wide" infra keys). Mitigation: `SecretScope.Platform` names a *scope* of secret ownership, not an actor; the two senses do not collide at any call site. The implementation PR audits cross-references when the new actor kind lands.

The `Guid` for the OSS operator's `PlatformUser` is pinned as a deterministic v5 UUID (decision 3 below). Every other `PlatformUser` row gets a freshly-minted Guid at provisioning time.

### 2. Connector-side user identity moves to the platform user

The `HumanConnectorIdentity` table that landed under ADR-0046 (#2408) is renamed to `PlatformUserConnectorIdentity` and rekeyed. The natural key shifts from `(tenant, human, connector, connector_user_id)` to `(tenant, platform_user, connector)`; the `connector_user_id` (e.g. the GitHub login) becomes a value column, and a single connector identity row per `(tenant, platform_user, connector)` is the new invariant. The `Human` row no longer carries any connector identity reference of its own — when a caller asks "what is this human's GitHub login?", the resolution path walks the `Human → PlatformUser` mapping (decision 7 below) and reads the platform user's row.

The user-config row holds:

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Surrogate. |
| `tenant_id` | `uuid` | `ITenantScopedEntity` per CONVENTIONS § 12. |
| `platform_user_id` | `uuid` | The `PlatformUserEntity.Id` this identity belongs to. |
| `connector_id` | `text` | Connector slug (e.g. `github`). Matches `IConnectorType.Slug`. |
| `config_json` | `jsonb` | Connector-contributed schema payload (e.g. `GitHubUserConfig`). |
| `created_at` | `timestamptz` | |
| `updated_at` | `timestamptz` | |

`config_json` is whatever shape the connector contributes through its extended seam (decision 4 below). For GitHub specifically the shape is `{ "username": string, "pat_secret_name": string?, "app_installation_override": long? }`. The "platform user identifies as login `x` on connector `y`" lookup that today's `IHumanConnectorIdentityResolver` answers is preserved by the new resolver name (`IPlatformUserConnectorIdentityResolver`); the existing `(tenant, connector, connector_user_id) → platform_user` query reads the `username` value column of the `config_json` payload.

Pre-#2487 `HumanConnectorIdentity` rows are dropped (decision 8 below). The new resolver supplies the same wire shape downstream.

### 3. The OSS operator's `PlatformUser` id is a deterministic v5 UUID

`OssPlatformUserIds.Operator` is a new `static class` in `src/Cvoya.Spring.Core/Tenancy/` (sibling to `OssTenantIds`). The single field `Operator` is the deterministic v5 UUID derived from:

```text
namespace = 00000000-0000-0000-0000-000000000000
label     = "cvoya/platform-user/oss-operator"
uuidv5    = ec86511f-0ae4-5532-8e46-e6be35939f25
```

Both dashed and no-dash 32-char forms are exposed as `const string` literals on the same class, mirroring `OssTenantIds.{Default,DefaultDashed,DefaultNoDash}`:

```csharp
public static class OssPlatformUserIds
{
    public static readonly Guid Operator = new("ec86511f-0ae4-5532-8e46-e6be35939f25");
    public const string OperatorDashed = "ec86511f-0ae4-5532-8e46-e6be35939f25";
    public const string OperatorNoDash = "ec86511f0ae455328e46e6be35939f25";
}
```

The recipe (namespace + label) is the documentation; the literal is the pin. Any v5 implementation against the same namespace + label reproduces the value — that is the property that makes the pin auditable from outside the platform. Reproduce with Python: `uuid.uuid5(uuid.UUID("00000000-0000-0000-0000-000000000000"), "cvoya/platform-user/oss-operator")`.

A new `static class` (not an additional field on `OssTenantIds`) is the right home: `OssTenantIds` names a kind of well-known id (tenant), and `OssPlatformUserIds` names a different kind (platform user). Co-locating them under one class would erode the discrimination ADR-0036 §1 worked to preserve.

Rejected (the same way ADR-0036 §8 rejected them for the tenant sentinel): `Guid.Empty` (reserved for "uninitialised / programmer error"), pattern-shaped low Guids like `…00000001` (claim sentinel space, no provenance), and any random-v4 chosen-by-coin-toss value (collision-free in practice but loses the "anyone can recompute it" property).

### 4. Connector contract extension: per-connector user-config schema

ADR-0045's connector contract is extended so each `IConnectorType` contributes *two* JSON schemas:

1. **Unit-binding config schema** (unchanged from today) — what `UnitGitHubConfig` is for the GitHub connector.
2. **User-config schema** (new) — what `GitHubUserConfig` is for the GitHub connector.

The contract surface adds one method or property to `IConnectorType` (`UserConfigSchema` / `GetUserConfigSchema()` — the exact shape lands with the implementation per ADR-0045's existing schema-contribution pattern). Connectors without a user-config concept (none in v0.1, but slated for v0.2: anything pure-inbound that needs no calling identity) return an empty schema; the user-config surface renders the connector as "no per-user configuration."

The `GitHubUserConfig` shape pinned by this ADR:

```jsonc
{
  "username": "string",                       // GitHub login (without leading @)
  "pat_secret_name": "string | null",          // tenant-secret-store reference, see decision 5
  "app_installation_override": "long | null"   // see decision 6
}
```

This is the same triple the umbrella issue sketched, with one structural commitment: the `app_installation_override` lives on user-config, not on the binding (decision 6).

### 5. PAT storage: tenant secret store; user-config row holds a secret name

The PAT itself never lives in the user-config row. It lives in the tenant secret store per [ADR-0003](0003-secret-inheritance-unit-to-tenant.md); the user-config row holds a free-form string secret name that addresses the entry.

**Naming convention.** A platform user's PAT secret for a given connector is stored under:

```text
platform-user/<platform-user-id-no-dash>/<connector-slug>/pat
```

For the OSS operator's GitHub PAT, that resolves to:

```text
platform-user/<OssPlatformUserIds.Operator no-dash>/github/pat
```

The convention is just a default the OSS portal / CLI generate when persisting a new PAT; operators who paste an existing secret name override it. The lookup is by name, not by structural decomposition of the path — the resolver does not parse the secret name to recover the platform-user id.

**Scope.** Secrets are written at `SecretScope.Tenant` so they are tenant-isolated and so the existing Unit → Tenant fall-through (ADR-0003 §1) lets unit-resident agents read them at use-time through the connector. The user-config row is the only place the secret *name* is persisted; the secret value lives only in the secret store.

### 6. Auth-resolution chain for GitHub writes

For every outbound GitHub call originating from a unit, the connector resolves the calling identity in strict order:

1. **App-installation token.** The connector queries the well-known SV App (or the BYO App per `GitHubConnectorOptions` — see decision 9) for an installation on `(owner, repo)`. If a single installation matches, the connector mints an installation token and uses it. If the calling platform user's user-config row has `app_installation_override` set to a specific installation id and that installation has access to `(owner, repo)`, the override wins over auto-resolution (tie-breaker for the rare multi-installation case).
2. **Platform-user PAT.** If step 1 yields no installation, the connector looks up the calling platform user's `PlatformUserConnectorIdentity` row for `connector=github`. If `pat_secret_name` is set, the connector resolves it through `ISecretResolver` (ADR-0003 surfaces Unit → Tenant fall-through automatically) and uses the PAT as a bearer token.
3. **Fail.** No installation, no PAT → structured error `GitHubAuthUnavailable` pointing the operator at the portal user-config page (or the equivalent CLI command). The error carries `(owner, repo)`, the resolved `platform_user_id`, and the install / PAT remediation hint; agents surface it to the caller rather than retrying.

**"Calling platform user."** The platform user the *agent* is acting on behalf of. The mapping from agent to platform user goes through the agent's owning unit's `Human → PlatformUser` resolution (decision 7); in OSS this always lands on `OssPlatformUserIds.Operator`. In cloud, it lands on whichever tenant user initiated the work surface that produced the agent's turn. Agent-initiated outbound writes that have no human caller in the chain (a permanent-hosting unit triaging on a webhook tick, for example) resolve to the unit's owning tenant's default platform user — in OSS, again the operator.

**Step 1 ambiguity handling.** If the App reports more than one installation for `(owner, repo)` (rare, occurs during App rotation when an org temporarily has two installations of the same App on overlapping repo sets), the connector rejects with `GitHubAmbiguousInstallation` and surfaces both installation ids. The platform user's `app_installation_override` is the documented disambiguation; the override applies here (turns the rejection into a determined choice) and at binding-create time (decision 10).

### 7. `Human → PlatformUser` resolution; OSS default

Every `Human` in OSS defaults to mapping onto `OssPlatformUserIds.Operator`. The mapping is read at agent-launch time and at every outbound-write resolution; the directory keeps an in-memory cache invalidated on `Human` writes and `PlatformUser` writes.

In OSS the mapping is trivial — one platform user, every human resolves to it — so the storage seam is the simplest shape that does the job: a derived projection (no explicit `human_to_platform_user` row required), with the option to add an explicit override table when cloud lands. Per-`Human` override of the default mapping is **out of scope for v0.1** (OUT2 in the umbrella).

The same chain is what makes agent-driven work appear "as the operator" in commit author, PR comment-as-the-human, and `--add-reviewer <human-login>` invocations: the agent's outbound surface resolves the relevant `Human`'s platform user, reads the platform user's connector identity (`username`), and renders the GitHub login.

### 8. `HumanConnectorIdentity` migration: drop and recreate under the new name

v0.1 is the freezing release. Per the project's standing clean-deploy rule (ADR-0036 § "Schema reset", ADR-0046 § 7), the `HumanConnectorIdentity` migration is rewritten in place rather than back-compat-migrated. Concretely:

- The `HumanConnectorIdentities` `DbSet`, entity, configuration, and unique-index are deleted.
- A new `PlatformUserConnectorIdentities` `DbSet`, entity, and configuration land with the natural key `(tenant_id, platform_user_id, connector_id)`. Unique-index drops `connector_user_id` from the key — the `username` lives inside `config_json` (decision 2 above).
- The migration is forward-only. Local development databases are reset on the v0.1 deploy; this is the standing v0.1 policy.

No row-level data migration. Pre-#2487 rows are not preserved.

### 9. BYO GitHub App stays supported; no new UX

Trust-sensitive operators who do not want cvoya-com to hold the SV App's private key configure their own App via `GitHubConnectorOptions`. The auth-resolution chain in decision 6 is identical for the BYO App — it just dispatches against a different App identity at step 1. No wizard or portal investment in BYO discovery, BYO manifest flow, or BYO setup; the existing `GitHubConnectorOptions` is the surface. The user-config page's "SV App detected" preview reads the active App and labels it (`SV App`, `BYO App <client-id>`) so operators understand which identity step 1 is using; that is the only BYO-facing UI change.

### 10. Webhook routing keys solely on `(owner, repo)`; cross-tenant binding rejected at create time

The webhook handler's resolver keys solely on `(owner, repo)` — the binding row's `(tenant, owner, repo)` triple implies the tenant scope, no `installation_id` involvement. UC4 (local-dev `gh webhook forward`) is resolved directly: the matcher's two cases collapse to one.

This requires a structural constraint at binding-create time: **a single `(owner, repo)` pair binds to at most one unit across the whole platform.** Two tenants attempting to bind the same `(owner, repo)` is rejected with `GitHubRepoBindingConflict`. The well-known SV App's installation model already prevents this in practice (an installation on `(owner, repo)` is anchored to one App instance — and one App instance backs one platform deployment), but the ADR pins the constraint at the binding level explicitly so the property does not depend on the App's behaviour. BYO App deployments inherit the same rule for the same reason: an inbound webhook payload for `(owner, repo)` has no way to discriminate which tenant should receive it if two tenants both claimed the repo.

### 11. `UnitGitHubConfig.AppInstallationId` removed; override lives on user-config

The umbrella sketched two shapes: (a) remove the column outright and reject on ambiguity, (b) keep a nullable `AppInstallationOverride` on the binding as a tie-breaker. This ADR chooses **(a)** for the binding plus **the override on user-config** (decision 4): `UnitGitHubConfig.AppInstallationId` is removed; `GitHubUserConfig.AppInstallationOverride` is the tie-breaker for the rare multi-installation case (decision 6, step 1).

The reasoning: the multi-installation case is a property of the *calling identity* (the App-rotation event for a given org), not of the *bound repo*. Lodging the override on the platform user's user-config row puts the disambiguation in the right place — each platform user can pin their own override during the rotation window; rotating the override away later affects only that platform user; the binding row stays clean. Lodging it on the binding would mean every tenant editing every binding during the rotation window; that is the wrong scope.

`UnitGitHubConfig.Owner` is also removed — it was structurally redundant (half of the qualified `Repo` name) and never used as a standalone identity (decision in Context paragraph 1).

### 12. CLI verb namespace renames; no shim

`spring human identity {set,list,remove}` renames to `spring user identity {set,list,remove}` in v0.1. v0.1 is the freezing release; the project's aggressive-cleanup convention applies: the old verbs are deleted outright with no deprecation alias, per the same pattern ADR-0039 §9, ADR-0046 § 7, and the standing house style established earlier in v0.1.

A new verb namespace `spring user config <connector>` is added alongside `spring user identity ...` for the user-config surface (UC6). The exact subcommand shape (`get`, `set`, `delete`, `list`) lands with the implementation per the existing CLI conventions; the umbrella plan-of-record bullets the breakdown.

### 13. OAuth-issued PAT acquisition is in scope for v0.1

The portal's user-config page and the CLI both offer OAuth as the default UX for acquiring a GitHub PAT, leveraging the existing `Auth/OAuth/` modules (`GitHubOAuthService`, `GitHubOAuthEndpoints`, session / state stores, `OctokitGitHubUserFetcher`, `OctokitGitHubUserScopeResolver`, `UserScopedRepositoryFilter`). The OAuth-issued token is persisted under the same naming convention as decision 5 — the user-config row records the secret name, the secret store holds the token.

**Default UX.**

- **Portal.** The user-config page's GitHub sub-surface defaults to an "Authorize with GitHub" button initiating the OAuth flow. A "Paste a PAT" link reveals the manual input. Both paths persist to the same secret name.
- **CLI.** `spring user config github authorize` opens a browser to the OAuth start URL and persists the resulting token. `spring user config github set-pat --from-stdin` is the manual path for operators in OAuth-degraded environments. Both write to the same secret name.

No new OAuth code lands; the work is wiring the existing modules through the new user-config endpoints and persisting the resulting token to the tenant secret store. Scopes requested are the minimum required for what an operator-driven SV unit does against a repo (`repo`, `read:user`); the exact scope set lands with the implementation, anchored to the existing `OctokitGitHubUserScopeResolver`.

### 14. API surface relocation

The endpoints registered under `/api/v1/tenant/humans/{humanId}/identities` (`HumanIdentityEndpoints`) relocate under `/api/v1/tenant/users/{platformUserId}/identities` (`PlatformUserIdentityEndpoints`). The verbs and the response shapes are preserved modulo the rename of the request DTOs (`HumanConnectorIdentityRequest` → `PlatformUserConnectorIdentityRequest`). The per-`Human` read-side envelope (`GET /api/v1/tenant/humans/{humanId}`) and the displayName / description PATCH stay where they are — those are unit-membership concerns ADR-0046 owns, distinct from the connector-identity surface.

A sibling route group `/api/v1/tenant/users/{platformUserId}/config/{connectorSlug}` carries the user-config surface (UC6). `GET` returns the current `config_json` against the connector's user-config schema; `PUT` validates and writes; `DELETE` removes. The OAuth start / callback routes already registered under `Auth/OAuth/GitHubOAuthEndpoints` are wired so the resulting token write targets the same `PlatformUserConnectorIdentity` row.

Existing `/api/v1/tenant/humans/{humanId}/identities` returns 410 Gone with a structured migration hint pointing at the new route shape, following the same pattern other v0.1 ADRs (ADR-0039 §9) established for retired endpoints. The 410 stub is deleted in a v0.2 cleanup pass; v0.1 ships it as the transition signal.

## Consequences

### Gains

- **The webhook bug becomes a free consequence.** Removing `installation_id` from the binding collapses the matcher's two cases to one keyed on `(owner, repo)`. `gh webhook forward` works locally without any matcher-side branching.
- **UC1 ships in v0.1.** Operators can run OSS SV against a public repo they don't admin: paste or OAuth-acquire a PAT into user-config, bind the unit to `owner/repo`, outbound writes go through step 2 of the auth chain. No SV App installation required.
- **Cumbersome `installation_id` discovery is gone.** Operators no longer look up an installation id in the new-unit wizard. The connector auto-resolves at use-time.
- **Connector-side user identity has one home.** The portal's user-config page and the CLI both surface a single per-platform-user surface where GitHub (today), Slack (tomorrow), Linear (later) all contribute their schema and credentials. Adding a connector adds a row to the surface, not a new storage seam.
- **Audit / activity rendering improves.** `OrchestrationDecision` and outbound-write audit rows can carry the resolved `platform_user_id` alongside `agent` and `unit`. "Which operator's identity wrote this PR?" has a structural answer.
- **Cloud overlay is unblocked.** The hosted multi-tenant overlay no longer needs a parallel "tenant user" model on top of `Human`; it adds explicit `Human → PlatformUser` mapping rows to the same seam OSS already uses.
- **The connector contract extension is reusable.** Every future connector contributes its user-config schema through the same seam; no per-connector storage code.

### Costs

- **Pre-v0.1 schema reset.** The `HumanConnectorIdentities` table is dropped and recreated. Local development databases are reset on the v0.1 deploy; the standing v0.1 policy. No row migration.
- **Five API routes relocate.** Portal and CLI clients re-target `/api/v1/tenant/users/...`. The 410 stub on the old route keeps integrators' next pull observably broken rather than silently mis-routed.
- **CLI surface churn.** Operators who built shell wrappers on `spring human identity ...` update them to `spring user identity ...`. The umbrella's release-notes paragraph names the rename.
- **OAuth wiring touches a permanent-hosting unit's launcher surface.** The OAuth callback persists a secret; the agent's connector reads it via the existing Unit → Tenant fall-through. Both surfaces already exist; the wiring is in the launcher / endpoint code.
- **One more actor kind to maintain.** The Address parser, the `display_name` validator, and the activity-log renderer all enumerate actor kinds today. Adding `platform-user` is a five-line change in each, but it is five lines in three files.
- **BYO App operators learn one rule.** "The user-config `app_installation_override` applies regardless of whether the active App is SV or BYO." The portal preview ("BYO App `<client-id>`") makes the active App observable so the rule is not abstract.

### Alternatives considered

- **Keep `installation_id` on the binding; add the `(owner, repo)` matcher fallback.** The localised fix the original #2487 sketched. Rejected: it leaves `installation_id` on the binding, where it represents calling identity, not bound-repo identity. The matcher path stays two-cased and the next webhook delivery without an `installation_id` reintroduces the bug class. Worse, it does not unblock UC1.
- **Keep connector identity on `Human`; mint a `Human` row per platform user.** Avoids the new actor kind. Rejected: OSS would mint one `Human` per package that declares humans, each carrying a copy of the operator's GitHub login. ADR-0046 explicitly chose "fresh `HumanEntity` per declaration" because humans are configuration entities, not principals; bolting "principal" semantics back onto `Human` re-fuses the two concerns ADR-0046 just split.
- **`AppInstallationOverride` on the binding instead of user-config.** The umbrella sketched both shapes. Rejected: the override is a property of the rotating App, which is a property of the calling identity, which is the platform user. Putting it on the binding scopes the override to a repo, which is the wrong axis for an App-rotation event.
- **Co-locate `OssPlatformUserIds.Operator` on `OssTenantIds`.** Considered for symmetry / file-count. Rejected: `OssTenantIds` names a kind of well-known id (tenant); `OssPlatformUserIds` names a different kind (platform user). Adding the field to `OssTenantIds` would conflate the kinds at the class level, which is exactly the discrimination ADR-0036 §1 worked to preserve.
- **Defer OAuth-issued PAT to v0.2.** The umbrella's pre-resolution-of-questions noted this was "out of scope (OUT3)" in the original framing. Brought into scope on @savasp's 2026-05-18 review: the existing `Auth/OAuth/` scaffolding is ~1.7 kloc that already does the work; surfacing it through the user-config endpoints is small, and shipping manual paste only would mean operators in cloud (where pasting a PAT is a worse UX than browser-OAuth) re-litigate the decision in v0.2.
- **`TenantUser` or `Principal` as the actor name.** Considered. Both rejected for the reasons in decision 1.

## Revisit criteria

Reopen this decision when any of the below holds:

- **Multi-platform-user OSS deployment lands** (the umbrella's OUT1). At that point the OSS-default `Human → PlatformUser` mapping needs a per-`Human` override surface; the seam is already in place but needs a concrete shape.
- **A connector wants per-binding, not per-platform-user, user-config.** None today, but a future connector that legitimately binds a calling identity to a *specific repo* (rather than to the platform user's GitHub login) reopens the decision-6 chain.
- **Cross-tenant `(owner, repo)` binding becomes a requirement.** Today rejected by decision 10. A future hosted-mode feature ("a single repo feeds two tenants' units") would reopen the constraint; the resolution is probably a per-installation routing key, not a relaxation of the binding-uniqueness rule.
- **Multi-installation overlap is no longer rare.** If multi-installation overlap becomes the common case (cross-org App rotation as a routine event), the per-user override stops being a tie-breaker and starts being load-bearing — at which point the resolver may need a structural way to address installations (e.g. an explicit installation-id field on the user-config row that is no longer "override" but "selection"). The Revisit-criteria stay open.
