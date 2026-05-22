# 0047 — TenantUser / human split; connector identity on the tenant user

- **Status:** Accepted — every authenticated principal of Spring Voyage is a `TenantUser` (new actor kind), distinct from the `Human` configuration entities that populate unit member rows. Display-side connector identity (GitHub login, Slack handle) is owned by the `TenantUser`, not by the unit binding and not by every `Human`. The `HumanConnectorIdentity` table is renamed and rekeyed onto `TenantUserConnectorIdentity` and reduced to a display-identity shape (no auth fields). Outbound auth lives on the unit binding: `UnitGitHubConfig` loses `Owner`, keeps `AppInstallationId`, and gains `PatSecretName`; exactly one of the two MUST be set at binding-create time. The binding's pinned credential is the only credential the unit uses against GitHub — no per-caller-tenant-user disambiguation at use-time. PAT storage uses ADR-0003's tenant secret store; the binding row holds a secret name, never the token value. In OSS, every `Human` defaults to the operator's well-known `TenantUser`, whose id is a deterministic v5 UUID pinned as a `const string` on a new `OssTenantUserIds` class (same shape as `OssTenantIds.Default`). The CLI verb namespace renames in v0.1 from `spring human identity {set,list,remove}` to `spring user identity {set,list,remove}` with no shim; binding subcommands gain a `--pat-secret-name` flag alongside `--installation-id`. OAuth-issued PAT acquisition is in scope for v0.1 and produces a tenant secret referenceable by a binding, plus an optional update to the calling `TenantUser`'s GitHub `username`. The connector contract (`IConnectorType`) is extended so a connector contributes both a unit-binding config schema (today) and a strictly display-identity user-config schema (new).
- **Date:** 2026-05-18
- **Owner:** @savasp (umbrella).
- **Closes:** [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487)
- **Tracks:** [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487) (umbrella; sub-issues filed against this umbrella after this ADR lands per the plan-of-record under [`docs/plan/v0.1/platform-user-split.md`](../plan/v0.1/platform-user-split.md)).
- **Amends:** [ADR-0036 — Single-identity model](0036-single-identity-model.md) — extends the enumeration of actor kinds in §1 to include `tenant-user`. The amendment line on ADR-0036 itself lands with the implementation PR that introduces the new actor kind, not this ADR. [ADR-0034 — Spring Voyage OSS dogfooding unit](0034-oss-dogfooding-unit.md) is **not** obsoleted — its §§ 4–5 atomic binding at template-apply time with an `installation_id` remains valid; the only change is that bindings may now also use `pat_secret_name` as an alternative auth choice. That is an extension of the binding shape, not an obsolescence of ADR-0034's invariants.
- **Related code:** `src/Cvoya.Spring.Connector.GitHub/UnitGitHubConfig.cs` (record losing `Owner`, gaining `PatSecretName`, keeping `AppInstallationId`), `src/Cvoya.Spring.Dapr/Data/Entities/HumanConnectorIdentityEntity.cs` (table being renamed onto `TenantUserConnectorIdentity` and shrunk), `src/Cvoya.Spring.Host.Api/Endpoints/HumanIdentityEndpoints.cs` (endpoints being relocated under `/api/v1/tenant/users/...`), `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/` (existing OAuth scaffolding to wire), `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs` (prose pattern mirrored for the new `OssTenantUserIds.Operator`), `src/Cvoya.Spring.Core/Security/PlatformRoles.cs` (existing `TenantUser` role name the new actor kind aligns with).
- **Related docs:** [`docs/plan/v0.1/platform-user-split.md`](../plan/v0.1/platform-user-split.md) (execution plan-of-record), [`docs/concepts/humans.md`](../concepts/humans.md) (updated under the plan), [`docs/concepts/tenants.md`](../concepts/tenants.md) (updated under the plan), [`docs/architecture/identifiers.md`](../architecture/data-and-identity.md) (updated under the plan).
- **Related ADRs:** [0003 — Secret inheritance Unit → Tenant](0003-secret-inheritance-unit-to-tenant.md) (governs how the binding's PAT secret is read), [0036 — Single-identity model](0036-single-identity-model.md) (Guid identity model amended here), [0034 — OSS dogfooding unit](0034-oss-dogfooding-unit.md) (binding shape extended here; not obsoleted), [0045 — Connector-domain-agnostic platform](0045-connector-domain-agnostic-platform.md) (connector contract extended here to contribute user-config schemas), [0046 — Unified members grammar](0046-unified-members-grammar.md) (the `Human` member kind whose display-side connector identities relocate here).

## Context

Spring Voyage's principal model conflated three orthogonal concerns and missed a fourth entirely, surfaced through a localised webhook-routing bug in [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487):

1. **Repo identity on the binding.** `UnitGitHubConfig` stored `Owner` and `Repo` as if both were addressing columns. Only the qualified `owner/repo` pair is the binding's addressing concern; `Owner` standing alone is half of a name, not an identity.
2. **Calling-identity bookkeeping conflated with display identity.** `UnitGitHubConfig.AppInstallationId` did the right thing — it pinned the credential the unit writes with — but the row offered no PAT alternative; operators wanting a public-repo flow with a PAT had no place to land it on the binding. Separately, the connector-side display identity (the human's GitHub login) lived on a different row that was being asked to hold credentials too. The two concerns — "what is the human's GitHub handle?" and "what credential does the unit use to push?" — were tangled.
3. **Connector-side user identity on `Human`.** ADR-0046 landed `HumanConnectorIdentity` keyed on `(tenant, human, connector, connector_user_id)`. Every `Human` row that participates in a unit could carry a GitHub login. But "humans" in OSS are configuration entities introduced by a package, not authenticated principals; they default to the operator and inherit the operator's identities. Keeping connector identity on the `Human` row meant either (a) duplicating the operator's GitHub login onto every `Human` in every installed package or (b) accepting that "this human's GitHub login" is meaningless for the OSS default. Neither shape composed.
4. **The missing concept — an authenticated tenant user.** The authenticated principal *of Spring Voyage itself* — the operator in OSS, tenant users in cloud — had no first-class home in the model. Connector-side display identity (your GitHub login, your Slack handle) belongs on that principal, not on every `Human` row that names them.

The trigger for the redesign was webhook routing. `gh webhook forward` delivers repo-shaped payloads without an `installation_id`, so `ResolveDestinationAsync` dropped the event. The localised fix would have been a `(owner, repo)` fallback on the matcher; the architectural fix is to make `(owner, repo)` *the* binding key for routing and to allow many bindings within a tenant to target the same `(owner, repo)`, with per-binding filters deciding processing. With that move, the matcher's two cases collapse to one, and "use case 1" of the original #2487 (PAT against a repo without the SV App installed) becomes a free consequence of allowing `PatSecretName` on the binding rather than a new concept.

The same shape will repeat for every future connector. Slack: the operator's Slack handle belongs on the operator for display / mention rendering; the unit's outbound Slack credential is a binding concern. Linear: same story. Codifying the `TenantUser` concept now makes adding the next connector additive — its display-identity schema slots into a uniform surface and its binding-side credential field has a uniform place to land.

ADR-0036 (§§ 1, 8) already established the Guid-keyed identity model and the deterministic-v5-UUID pattern for the OSS default tenant id. This ADR is the next layer above: the principal kind that complements the tenant, and the storage / resolution rules for display-side identities on that principal plus auth-side credentials on the binding.

## Decision

### 1. `TenantUser` is a new actor kind

A `TenantUser` is an authenticated principal of Spring Voyage **scoped to one tenant**. The OSS deployment ships with exactly one (the operator, in the OSS default tenant). The cloud deployment carries many per tenant. The actor-kind enumeration that ADR-0036 §1 pinned (unit, agent, human, connector, tenant) is extended in the implementation PR to include `tenant-user`; every property ADR-0036 attaches to actor kinds (Guid identity, `display_name` is presentation-only, `display_name` cannot parse as a Guid, membership graph is the addressing fabric) applies unchanged.

**Filename note.** The ADR filename `0047-platform-user-human-split.md` is retained from the proposal stage for PR continuity (the open PR references this path). The terminology inside the document is the canonical one — `TenantUser` throughout — and downstream artefacts (concept docs, the identifiers doc, the plan-of-record) use the `TenantUser` term in body text regardless of plan-file path.

**Name choice — `TenantUser`.** The term aligns with the existing `PlatformRoles.TenantUser` role name (`src/Cvoya.Spring.Core/Security/PlatformRoles.cs:46`), whose docstring already reads "uses Spring Voyage inside a tenant: messaging, observing, units / agents, dashboard, conversations." That role and this actor kind name the same idea from two sides; landing the actor kind under the same name keeps the two synchronised.

**A `TenantUser` is per tenant; the same human is two different rows.** A `TenantUser` is bound to exactly one tenant. The natural key is `(tenant_id, auth_subject)`, where `auth_subject` is the OAuth `sub` claim (nullable in OSS dev where the operator may not OAuth-authenticate — there the row is pinned by the deterministic operator UUID below). The same Google account authenticated against tenant T1 and tenant T2 produces **two distinct `TenantUser` rows**, each with its own connector-identity history. After OAuth login the system looks up every `TenantUser` row whose `auth_subject` matches the OAuth `sub`; the caller picks a tenant context; subsequent requests operate in that tenant context. Cross-tenant identity sharing is explicitly not a thing — there is no "global user" concept and no "global GitHub handle" concept. A user who appears in two tenants and uses GitHub in both has two `TenantUserConnectorIdentity` rows for GitHub, one per tenant, and may legitimately have different handles configured per tenant.

The candidate name `PlatformUser` was considered and rejected: in the hosted service a user is always associated with a tenant; there is no user that exists outside a tenant's boundary. `PlatformUser` would suggest a platform-global identity, which the model deliberately does not have. The candidate name `Principal` was considered and rejected as too generic — the model already uses "actor" as the umbrella term per ADR-0036 §1; "principal" in software-security idiom can mean any authenticated subject (a service account, a header-borne claim). `TenantUser` names a *kind* of principal under the actor umbrella.

The `Guid` for the OSS operator's `TenantUser` is pinned as a deterministic v5 UUID (decision 3 below). Every other `TenantUser` row gets a freshly-minted Guid at provisioning time.

### 2. Display-side connector identity moves to the tenant user

The `HumanConnectorIdentity` table that landed under ADR-0046 (#2408) is renamed to `TenantUserConnectorIdentity` and rekeyed. The natural key shifts from `(tenant, human, connector, connector_user_id)` to `(tenant, tenant_user, connector)`; the row holds the connector-side display handles for that `(tenant_user, connector)` pair and **nothing else** — no PAT, no installation override, no auth fields. The `Human` row no longer carries any connector identity reference of its own — when a caller asks "what is this human's GitHub login?", the resolution path walks the `Human → TenantUser` mapping (decision 7 below) and reads the tenant user's row.

The row shape is intentionally narrow:

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | Surrogate. |
| `tenant_id` | `uuid` | `ITenantScopedEntity` per CONVENTIONS § 12. |
| `tenant_user_id` | `uuid` | The `TenantUserEntity.Id` this identity belongs to. |
| `connector_id` | `text` | Connector slug (e.g. `github`). Matches `IConnectorType.Slug`. |
| `username` | `text` | The connector-side login (e.g. GitHub `octocat`, Slack `@alice`). No leading `@`. |
| `display_handle` | `text?` | Optional human-friendly rendering (e.g. "Alice Smith (@alice)"). Falls back to `username` when null. |
| `created_at` | `timestamptz` | |
| `updated_at` | `timestamptz` | |

There is no `config_json` blob; the schema is fixed across connectors. A connector-contributed user-config schema (decision 4) describes which fields a connector's portal / CLI form surfaces — for GitHub today, just `username` and optionally `display_handle` — but the storage shape is uniform.

The row is **strictly display-identity**. Its single job is answering "who is this SV human in connector X terms?" for:

- `@`-mention rendering in PR comments and Slack messages,
- `--add-reviewer <login>` invocations against the GitHub API,
- attribution rendering in `OrchestrationDecision` evidence and activity-log rows.

The OAuth flow (decision 13) may populate this row's `username` from the GitHub user-info response as a UX nicety — but the row never holds the OAuth-issued token. That token lands in the tenant secret store as an auth-side artefact for binding consumption (decision 5).

The "tenant user identifies as login `x` on connector `y`" lookup that today's `IHumanConnectorIdentityResolver` answers is preserved by the new resolver name (`ITenantUserConnectorIdentityResolver`); the existing `(tenant, connector, username) → tenant_user` query reads the `username` column directly.

Pre-#2487 `HumanConnectorIdentity` rows are dropped (decision 8 below). The new resolver supplies the same wire shape downstream.

### 3. The OSS operator's `TenantUser` id is a deterministic v5 UUID

`OssTenantUserIds.Operator` is a new `static class` in `src/Cvoya.Spring.Core/Tenancy/` (sibling to `OssTenantIds`). The single field `Operator` is the deterministic v5 UUID derived from:

```text
namespace = 00000000-0000-0000-0000-000000000000
label     = "cvoya/tenant-user/oss-operator"
uuidv5    = 5c4c8e29-d91b-5b50-8651-64536cfb68ee
```

Both dashed and no-dash 32-char forms are exposed as `const string` literals on the same class, mirroring `OssTenantIds.{Default,DefaultDashed,DefaultNoDash}`:

```csharp
public static class OssTenantUserIds
{
    public static readonly Guid Operator = new("5c4c8e29-d91b-5b50-8651-64536cfb68ee");
    public const string OperatorDashed = "5c4c8e29-d91b-5b50-8651-64536cfb68ee";
    public const string OperatorNoDash = "5c4c8e29d91b5b50865164536cfb68ee";
}
```

The recipe (namespace + label) is the documentation; the literal is the pin. Any v5 implementation against the same namespace + label reproduces the value — that is the property that makes the pin auditable from outside the platform. Reproduce with Python: `uuid.uuid5(uuid.UUID("00000000-0000-0000-0000-000000000000"), "cvoya/tenant-user/oss-operator")`.

A new `static class` (not an additional field on `OssTenantIds`) is the right home: `OssTenantIds` names a kind of well-known id (tenant), and `OssTenantUserIds` names a different kind (tenant user). Co-locating them under one class would erode the discrimination ADR-0036 §1 worked to preserve.

Rejected (the same way ADR-0036 §8 rejected them for the tenant sentinel): `Guid.Empty` (reserved for "uninitialised / programmer error"), pattern-shaped low Guids like `…00000001` (claim sentinel space, no provenance), and any random-v4 chosen-by-coin-toss value (collision-free in practice but loses the "anyone can recompute it" property).

### 4. Connector contract extension: per-connector display-identity schema

ADR-0045's connector contract is extended so each `IConnectorType` contributes *two* JSON schemas:

1. **Unit-binding config schema** (unchanged from today in spirit — see decision 11 for the GitHub-specific shape change) — what `UnitGitHubConfig` is for the GitHub connector. This schema describes the binding's addressing fields (e.g. `repo`) and its auth-side fields (e.g. `app_installation_id`, `pat_secret_name`).
2. **User-config schema** (new) — strictly the display-identity surface for the `(tenant_user, connector)` row. For the GitHub connector this is `{ username, display_handle? }`. There is no PAT field, no installation override, no auth field of any kind in this schema.

The contract surface adds one method or property to `IConnectorType` (`UserConfigSchema` / `GetUserConfigSchema()` — the exact shape lands with the implementation per ADR-0045's existing schema-contribution pattern). Connectors without a display-identity concept return an empty schema; the surface renders the connector as "no per-user configuration."

The `GitHubUserConfig` shape pinned by this ADR:

```jsonc
{
  "username": "string",                       // GitHub login (without leading @)
  "display_handle": "string | null"           // optional human-friendly rendering
}
```

No `pat_secret_name`, no `app_installation_override`. Those concerns are binding-side (decision 11).

### 5. Auth-side credential: tenant secret store; binding row holds a secret name

When a binding chooses the PAT auth path (decision 11), the PAT itself never lives in the binding row. It lives in the tenant secret store per [ADR-0003](0003-secret-inheritance-unit-to-tenant.md); the binding row holds a free-form string secret name (`pat_secret_name`) that addresses the entry.

**Provenance is irrelevant to the binding.** The PAT under `pat_secret_name` is a tenant secret. It may have been issued by a SV `TenantUser`'s OAuth flow, pasted by an operator from a bot account, generated by a service organisation, or sourced from an entirely external identity that has no `TenantUser` row at all. The binding only knows "this is the credential SV uses when this unit calls GitHub." There is no concept of changing the binding's auth based on which `TenantUser` initiated the work — every outbound call from the unit uses the binding's pinned credential, full stop.

**Naming convention.** A binding's PAT secret is stored under a binding-scoped name:

```text
binding/<binding-id-no-dash>/<connector-slug>/pat
```

Binding-scoped over unit-scoped (`unit/<unit-id-no-dash>/…`) because a single unit may have multiple bindings against the same connector type for different `(owner, repo)` pairs, each potentially using a different credential; the binding id is the unique key that always disambiguates. For the OSS dogfooding unit's GitHub binding, the resolved name is `binding/<binding-id-no-dash>/github/pat`.

The convention is the default the OSS portal / CLI generate when persisting a new PAT; operators who paste an existing secret name override it. The lookup is by name, not by structural decomposition of the path — the resolver does not parse the secret name to recover the binding id.

**Scope.** Secrets are written at `SecretScope.Tenant` so they are tenant-isolated and so the existing Unit → Tenant fall-through (ADR-0003 §1) lets unit-resident agents read them at use-time through the connector. The binding row is the only place the secret *name* is persisted; the secret value lives only in the secret store.

### 6. Auth resolution: read the binding, mint or resolve, send

For every outbound GitHub call originating from a unit, the connector reads the unit's binding row and dispatches based on the single auth field that is set:

- If `app_installation_id` is set, the connector mints an installation token against the configured App (the well-known SV App, or the BYO App per `GitHubConnectorOptions` — see decision 9) for that installation and uses it.
- If `pat_secret_name` is set, the connector resolves the secret through `ISecretResolver` (ADR-0003 surfaces Unit → Tenant fall-through automatically) and uses the PAT as a bearer token.

There is no fall-through, no chain, no use-time ambiguity disambiguation. The binding-create-time gate (decision 11) guarantees exactly one of the two fields is set, so the dispatch is a single read.

There is no "calling tenant user" lookup in the auth path. The agent-acting-on-behalf-of-tenant-user-X / Y distinction does not enter auth resolution. Whether the work was kicked off by the OSS operator, by a cloud `TenantUser` in tenant T1, or by a webhook tick with no human caller, the binding's pinned credential is what the connector uses.

The structured error vocabulary collapses to one auth-time signal: `GitHubBindingAuthMissing`, raised if `ISecretResolver` cannot find the secret named by `pat_secret_name` or if the App-installation token-mint fails. The binding-create-time gate prevents the "neither field set" structural case from ever reaching the connector.

### 7. `Human → TenantUser` resolution; display / mention / attribution use

The `Human → TenantUser` mapping is the **display / mention / attribution** seam, not an auth seam. When a unit's agent renders `@<human-name>` in a PR comment, the agent walks `Human → TenantUser → TenantUserConnectorIdentity for connector=github` to find the `username`, and emits the `@username` token. When the same agent calls `--add-reviewer <login>` it follows the same lookup. The outbound API call's credential is, separately and unconnectedly, the binding's pinned credential per decision 6.

In OSS every `Human` defaults to mapping onto `OssTenantUserIds.Operator`. The mapping is read at agent-launch time and at every outbound display-render call site; the directory keeps an in-memory cache invalidated on `Human` writes and `TenantUser` writes.

In OSS the mapping is trivial — one tenant user, every human resolves to it — so the storage seam is the simplest shape that does the job: a derived projection (no explicit `human_to_tenant_user` row required), with the option to add an explicit override table when cloud lands and per-`Human` overrides become a feature. Per-`Human` explicit mapping override is **out of scope for v0.1** (OUT2 in the umbrella).

### 8. `HumanConnectorIdentity` migration: drop and recreate under the new name and shape

v0.1 is the freezing release. Per the project's standing clean-deploy rule (ADR-0036 § "Schema reset", ADR-0046 § 7), the `HumanConnectorIdentity` migration is rewritten in place rather than back-compat-migrated. Concretely:

- The `HumanConnectorIdentities` `DbSet`, entity, configuration, and unique-index are deleted.
- A new `TenantUserConnectorIdentities` `DbSet`, entity, and configuration land with the natural key `(tenant_id, tenant_user_id, connector_id)` and the shrunk shape from decision 2 (`username`, `display_handle?`; no auth fields, no `config_json`).
- `UnitGitHubConfig` migration **drops `Owner`** and **adds `PatSecretName`**; `AppInstallationId` stays in place.
- The migration is forward-only. Local development databases are reset on the v0.1 deploy; this is the standing v0.1 policy.

No row-level data migration. Pre-#2487 rows are not preserved.

### 9. BYO GitHub App stays supported; no new UX

Trust-sensitive operators who do not want cvoya-com to hold the SV App's private key configure their own App via `GitHubConnectorOptions`. Decision 6 dispatches identically for the BYO App at the App-installation branch — it just mints against a different App identity. No wizard or portal investment in BYO discovery, BYO manifest flow, or BYO setup; the existing `GitHubConnectorOptions` is the surface. The binding's auth-choice preview ("SV App detected" / "BYO App `<client-id>` detected" / "Will use the configured PAT") makes the active credential observable.

### 10. Webhook routing fans out within a tenant; cross-tenant `(owner, repo)` collision rejected at binding-create time

The webhook handler keys on `(owner, repo)` and **fans out to every matching binding within the receiving tenant**. Within a single tenant, multiple unit bindings may target the same `(owner, repo)` pair — this is a supported configuration, not a constraint violation. Each binding's existing filters (`include_labels`, `exclude_labels`, `include_authors`, `include_paths`) decide whether that unit processes the event. The use case is concrete: a `frontend-team` unit and a `backend-team` unit, both bound to the same monorepo, each filtering on its own label set, must both receive the event and decide independently.

The matcher's prior two cases (`(installation_id, owner, repo)` and `(owner, repo)`) collapse to one (`(owner, repo)` within the receiving tenant). UC4 (local-dev `gh webhook forward` payloads without an `installation_id`) is resolved directly.

**Cross-tenant `(owner, repo)` collision is still rejected at binding-create time.** Once `installation_id` is out of the routing key, an inbound webhook payload for `(owner, repo)` carries no tenant signal; there is no way to discriminate which tenant should receive the event if two tenants both claimed the repo. The constraint name is `GitHubCrossTenantRepoBindingConflict` (renamed from the umbrella sketch's tenant-blind `GitHubRepoBindingConflict` to make the scope explicit in the error message). The well-known SV App's installation model already prevents this in practice in cloud (an installation on `(owner, repo)` is anchored to one App instance — and one App instance backs one tenant); BYO App deployments inherit the same rule structurally.

### 11. `UnitGitHubConfig` shape: `Owner` removed, `PatSecretName` added, `AppInstallationId` kept

`UnitGitHubConfig.Owner` is removed — it was structurally redundant (half of the qualified `Repo` name) and never used as a standalone identity (decision in Context paragraph 1).

`UnitGitHubConfig` retains `AppInstallationId` (it was already there, doing the right job — pinning the App installation the unit writes through) and gains `PatSecretName` (decision 5's secret reference). **Exactly one of `AppInstallationId` and `PatSecretName` MUST be set at binding-create time.** Both null is rejected (no credential → outbound writes impossible). Both set is rejected (ambiguous credential — the binding-create endpoint refuses to silently pick one). The rejection error is `GitHubBindingAuthRequired` (neither set) / `GitHubBindingAuthAmbiguous` (both set), surfaced at the endpoint, the CLI command, and the portal wizard so operators see it before persistence.

The choice between the two paths is a tenant-administrator decision made at binding time. An admin with the privilege to create units and configure connectors chooses either the App-installation route (when the SV App or a BYO App is installed on the repo) or the PAT route (when the operator prefers a PAT — for example a public-repo flow against a repo the SV App is not installed on, or a deliberate operator-controlled credential). Once chosen, the binding's credential is pinned; the connector always uses it.

### 12. CLI verb namespace renames; no shim; binding subcommands grow `--pat-secret-name`

`spring human identity {set,list,remove}` renames to `spring user identity {set,list,remove}` in v0.1. v0.1 is the freezing release; the project's aggressive-cleanup convention applies: the old verbs are deleted outright with no deprecation alias, per the same pattern ADR-0039 §9, ADR-0046 § 7, and the standing house style established earlier in v0.1.

`spring user identity` manages the calling `TenantUser`'s display-identity rows (decision 2). There is **no** separate `spring user config <connector>` verb namespace — the user-config surface is purely display identity, and `spring user identity` covers the whole thing. Adding a parallel verb namespace would duplicate the surface.

Binding-side commands gain a `--pat-secret-name <name>` flag alongside the existing `--installation-id <id>`. The binding-create command rejects at parse time if neither or both are supplied, with hints pointing at the binding-auth model. `--owner` is removed (the field is removed from the binding row per decision 11); the `--repo` flag now accepts the qualified `owner/repo` form only, and rejects unqualified inputs with a parse-time hint.

### 13. OAuth-issued PAT acquisition is in scope for v0.1

The OAuth flow's purpose in v0.1 is twofold:

1. **Mint a PAT-equivalent token and persist it as a tenant secret** under the §5 naming convention, ready for a binding to reference via `pat_secret_name`.
2. **Optionally populate the calling `TenantUser`'s `TenantUserConnectorIdentity.username` for the GitHub connector** from the OAuth user-info response, as a UX nicety so the user does not have to type their own GitHub login.

The flow can be initiated from the portal (Authorize button on the user-identity surface) or from the CLI. The wizard step that creates a binding **pre-fills `pat_secret_name`** with the new secret's name when the operator initiated the flow from that wizard, so the binding-create call lands without re-asking for the secret name.

**Manual paste is the alternate path** — the portal's "Paste a PAT" link and the CLI's `--from-stdin` flag target the same secret-name output but skip the OAuth dance (and therefore skip the auto-populate of `TenantUserConnectorIdentity.username`; the operator updates `username` separately if desired).

No new OAuth code lands; the work is wiring the existing `Auth/OAuth/` modules (`GitHubOAuthService`, `GitHubOAuthEndpoints`, session / state stores, `OctokitGitHubUserFetcher`, `OctokitGitHubUserScopeResolver`, `UserScopedRepositoryFilter`) through the new endpoints. Scopes requested are the minimum required for what an operator-driven SV unit does against a repo (`repo`, `read:user`); the exact scope set lands with the implementation, anchored to the existing `OctokitGitHubUserScopeResolver`.

### 14. API surface relocation

The endpoints registered under `/api/v1/tenant/humans/{humanId}/identities` (`HumanIdentityEndpoints`) relocate under `/api/v1/tenant/users/{tenantUserId}/identities` (`TenantUserIdentityEndpoints`). The verbs and the response shapes are preserved modulo the rename of the request DTOs (`HumanConnectorIdentityRequest` → `TenantUserConnectorIdentityRequest`) and the shrunk shape (display-identity fields only). The per-`Human` read-side envelope (`GET /api/v1/tenant/humans/{humanId}`) and the displayName / description PATCH stay where they are — those are unit-membership concerns ADR-0046 owns, distinct from the connector-identity surface.

There is **no sibling user-config route group** — user-config is display identity, fully covered by the `/identities` routes above. Adding a parallel `/config/{connectorSlug}` group would duplicate the surface.

Binding endpoints (`/api/v1/tenant/units/{unitId}/connector-bindings/...`) stay where they are; their request and response DTOs grow a `pat_secret_name` field alongside the existing `app_installation_id`, and the endpoint enforces the "exactly one" rule from decision 11. The OAuth start / callback routes already registered under `Auth/OAuth/GitHubOAuthEndpoints` are wired so the resulting token write targets a tenant secret named per decision 5 and, when initiated from the binding wizard, returns a payload the wizard uses to pre-fill the binding-create call.

Existing `/api/v1/tenant/humans/{humanId}/identities` returns 410 Gone with a structured migration hint pointing at the new route shape, following the same pattern other v0.1 ADRs (ADR-0039 §9) established for retired endpoints. The 410 stub is deleted in a v0.2 cleanup pass; v0.1 ships it as the transition signal.

## Consequences

### Gains

- **The webhook bug becomes a free consequence.** Removing `installation_id` from the routing key collapses the matcher's two cases to one keyed on `(owner, repo)` within the receiving tenant. `gh webhook forward` works locally without any matcher-side branching.
- **Webhook fan-out within tenant.** Multiple unit bindings within a tenant can target the same `(owner, repo)`; the handler fans out and per-binding filters decide processing. Use cases like "frontend-team and backend-team units both bound to the same monorepo with different label filters" now work.
- **UC1 ships in v0.1.** Operators can run OSS SV against a public repo they don't admin: paste or OAuth-acquire a PAT into a tenant secret, configure the binding with `pat_secret_name`, outbound writes go through the PAT branch of decision 6. No SV App installation required. The binding configures the PAT, not user-config.
- **Cumbersome `installation_id` discovery is gone for the PAT path.** Operators who choose the PAT path never look up an installation id. The App-installation path still requires it, exactly as ADR-0034 already required.
- **Display-side connector identity has one home.** The portal's user-identity surface and the CLI both target a single per-`TenantUser` row per connector. Adding a connector adds a row to the surface, not a new storage seam.
- **Audit / activity rendering improves.** Display-side `@username` rendering, `--add-reviewer <login>`, and attribution lines all walk the same `Human → TenantUser → TenantUserConnectorIdentity` chain regardless of which credential the binding pushed with. "Which human is this?" has a structural answer; "which credential wrote this PR?" is the binding row.
- **Cloud overlay is unblocked.** The hosted multi-tenant overlay no longer needs a parallel "tenant user" model on top of `Human`; the `TenantUser` concept is the seam, and the per-tenant scoping is built in from day one.
- **The connector contract extension is reusable.** Every future connector contributes its display-identity schema through the same seam; no per-connector storage code.

### Costs

- **Pre-v0.1 schema reset.** The `HumanConnectorIdentities` table is dropped and recreated. Local development databases are reset on the v0.1 deploy; the standing v0.1 policy. No row migration.
- **Five API routes relocate.** Portal and CLI clients re-target `/api/v1/tenant/users/...`. The 410 stub on the old route keeps integrators' next pull observably broken rather than silently mis-routed.
- **CLI surface churn.** Operators who built shell wrappers on `spring human identity ...` update them to `spring user identity ...`; binding-create wrappers add `--pat-secret-name` or `--installation-id` as required. The umbrella's release-notes paragraph names the renames.
- **OAuth wiring touches the binding-create wizard.** The wizard step that creates a binding accepts an OAuth-completed handoff and pre-fills `pat_secret_name`. Both surfaces already exist; the wiring is in the wizard / endpoint code.
- **One more actor kind to maintain.** The Address parser, the `display_name` validator, and the activity-log renderer all enumerate actor kinds today. Adding `tenant-user` is a few-line change in each.
- **Tenant admins make an auth choice at binding time.** App-installation or PAT? The wizard's auth-choice step makes the choice explicit and explains the trade-off (App: best for repos you admin; PAT: best for public repos or operator-controlled credentials). The cost is one extra wizard step; the gain is no use-time auth ambiguity.

### Alternatives considered

- **Keep `installation_id` on the binding (only); add the `(owner, repo)` matcher fallback.** The localised fix the original #2487 sketched. Rejected: it leaves the binding without a PAT option, so UC1 (public-repo without SV App) does not ship. The webhook fix lands, but the broader model stays tangled.
- **Keep connector identity on `Human`; mint a `Human` row per `TenantUser`.** Avoids the new actor kind. Rejected: OSS would mint one `Human` per package that declares humans, each carrying a copy of the operator's GitHub login. ADR-0046 explicitly chose "fresh `HumanEntity` per declaration" because humans are configuration entities, not principals; bolting "principal" semantics back onto `Human` re-fuses the two concerns ADR-0046 just split.
- **Per-`TenantUser` PAT held on user-config; binding auth derived from "the calling tenant user."** The PR review feedback rejected this on 2026-05-18: the semantics are wrong. The calling `TenantUser`'s GitHub handle must be per-`TenantUser` for display, but the credential a unit pushes with is a binding concern, not a per-caller concern. The same unit acting on behalf of two different `TenantUser`s must still push with the binding's pinned credential. A PAT may belong to an entity that is not a `TenantUser` of SV at all (a bot account, a service org). "Use the PAT of whichever `TenantUser` initiated the work" silently changes the wire identity GitHub sees based on caller, which is the wrong contract — the binding represents the unit's permanent operating identity against the upstream system, and that identity must not float per-caller.
- **`AppInstallationOverride` on the binding or on user-config.** The umbrella sketched both shapes. Both rejected: with the binding's `AppInstallationId` directly setting the installation when the App path is chosen, there is no "auto-resolve then override" case left to disambiguate. The binding pins the installation it wants, full stop.
- **Co-locate `OssTenantUserIds.Operator` on `OssTenantIds`.** Considered for symmetry / file-count. Rejected: `OssTenantIds` names a kind of well-known id (tenant); `OssTenantUserIds` names a different kind (tenant user). Adding the field to `OssTenantIds` would conflate the kinds at the class level, which is exactly the discrimination ADR-0036 §1 worked to preserve.
- **Defer OAuth-issued PAT to v0.2.** The umbrella's pre-resolution-of-questions noted this was "out of scope (OUT3)" in the original framing. Brought into scope on the 2026-05-18 review of this ADR: the existing `Auth/OAuth/` scaffolding is ~1.7 kloc that already does the work; surfacing it through the binding-create wizard is small, and shipping manual paste only would mean operators in cloud (where pasting a PAT is a worse UX than browser-OAuth) re-litigate the decision in v0.2.
- **`PlatformUser` or `Principal` as the actor name.** Considered. Both rejected for the reasons in decision 1.

## Revisit criteria

Reopen this decision when any of the below holds:

- **Multi-`TenantUser` OSS deployment lands** (the umbrella's OUT1). At that point the OSS-default `Human → TenantUser` mapping needs a per-`Human` override surface; the seam is already in place but needs a concrete shape.
- **A connector wants per-binding user-side display schema beyond the uniform `{username, display_handle?}`.** None today, but a future connector that legitimately needs richer display fields per `(tenant_user, connector)` reopens the row shape.
- **Cross-tenant `(owner, repo)` binding becomes a requirement.** Today rejected by decision 10. A future hosted-mode feature ("a single repo feeds two tenants' units") would reopen the constraint; the resolution is probably an explicit tenant-routing key on inbound webhook payloads, not a relaxation of the binding-uniqueness rule.
- **A real use case for per-`TenantUser` binding auth emerges.** If a concrete need surfaces (e.g. "each agent runs as the inviting `TenantUser`'s PAT for attribution-against-personal-account purposes"), reopen the auth-on-binding decision (§6). Today the architectural answer is no — the binding pins the credential and every caller uses it.
