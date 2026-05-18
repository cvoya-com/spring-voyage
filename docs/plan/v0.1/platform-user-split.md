# TenantUser / human split + connector identity location — execution plan

**Initiative.** Implementation of [ADR-0047 — TenantUser / human split; connector identity on the tenant user](../../decisions/0047-platform-user-human-split.md). Tracked under the umbrella [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487) on milestone `v0.1`. The umbrella supersedes the original single-PR shape of #2487 (a localised webhook fix) and pulls forward `TenantUser` pieces originally scoped for v0.2 / v0.3.

This plan enumerates the **phase sequence by impact zone**, with the structural `blockedBy` ordering set at the phase grain. Per the umbrella's step-0 contract, individual issues are not pre-allocated here — each phase fans out into 1–3 single-PR sub-issues after this plan + ADR land and the ADR is moved to Accepted. The grain is "one phase, one or a handful of consecutive PRs."

**Filename note.** The plan file is named `platform-user-split.md` for PR continuity (the open PR commit references this filename). The terminology inside is the canonical one — `TenantUser` throughout. Downstream artefacts (concept docs, the identifiers doc) use the `TenantUser` term in body text regardless of this file's path.

## Conventions

These mirror the conventions established in [`docs/plan/v0.1/units-are-agents.md`](units-are-agents.md), the canonical v0.1 cross-cutting plan format. The summary form:

- **One task = one PR.** No batching, no "while we're here." If a phase exceeds one PR, the phase fans out into consecutive sub-issues at the obvious seam.
- **Acceptance criteria are testable.** Build clean, test green, file exists, function behaves — mechanically verifiable.
- **Tasks reference the ADR by section, not by paraphrase.** "See ADR-0047 §6" is enough; agents read the ADR rather than the paraphrase.
- **`blockedBy` is structural.** Set via GitHub's native blocked-by edges through `gh-app`, not stated in issue prose. The umbrella's sub-issue panel and each task's blocked-by panel surface the order; bodies stay thin.
- **Aggressive cleanup; no back-compat.** Deleted code's tests are deleted too. No shims, no `_legacyFoo` markers, no deprecation paths beyond what ADR-0047 §§ 8, 12, 14 list (the `HumanConnectorIdentities` table drop, the CLI rename, the 410 stub on the old API route).
- **Sub-issues file against the umbrella with `area:tenant-user-split`.** Type `Task` (or `Feature` for the larger phases). Milestone `v0.1`.

## Phases at a glance

| Phase | Impact zone | Acceptance signal | Depends on |
|---|---|---|---|
| A | Domain model + EF migration: `TenantUser`, `TenantUserConnectorIdentity` rekey + shape shrink, `UnitGitHubConfig` shape change | Build + tests green; migration applies cleanly; `OssTenantUserIds.Operator` constant pinned | — (foundation) |
| B | API surface: `/api/v1/tenant/users/*` identities routes; retire `/api/v1/tenant/humans/{id}/identities` with 410 stub; `UnitGitHubConfig{Request,Response}` shape change (drop `owner`, add `pat_secret_name`); OpenAPI regen | Integration tests pin every new route + retire stub; OpenAPI-diff clean | A |
| C | Connector contract extension: `IConnectorType` gains user-config (display-identity) schema seam; GitHub connector contributes `GitHubUserConfig` schema (`{ username, display_handle? }`) | Build clean; schema-contribution test covers GitHub and a no-op fixture connector | A |
| D | GitHub connector auth resolution: read the binding, dispatch to App-installation or PAT branch; structured error `GitHubBindingAuthMissing` | Unit + integration tests cover each branch and the missing-secret error; existing GitHub-write call sites green | C |
| E | Webhook handler simplification: `(owner, repo)` keying within receiving tenant; many bindings per `(tenant, owner, repo)` fan-out; binding-create-time `GitHubCrossTenantRepoBindingConflict` rejection | UC4 e2e (gh-webhook-forward) green; cross-tenant binding-create rejection test green; in-tenant fan-out test (two bindings, different label filters) green | D |
| F | OAuth wiring (UC8): existing `Auth/OAuth/` modules surfaced; OAuth-issued token persisted as a tenant secret under the binding-scoped naming convention; binding-create wizard pre-fills `pat_secret_name`; OAuth user-info optionally populates the `TenantUser`'s GitHub `username` | E2E: portal "Authorize with GitHub" round-trip persists a secret + pre-fills the wizard + optionally updates display identity | B + ADR-0003 (in place) |
| G | CLI: `spring user identity {set,list,remove}`; binding subcommands gain `--pat-secret-name` and reject neither/both; `--owner` removed; `--repo` accepts qualified form only | CLI scenarios cover the new verbs + the dropped flags' parse-time rejections + the binding-auth gate; help text clean | B |
| H | Portal: user-identity page is "your handles" only (no PAT input); new-unit wizard GitHub step gains an auth-choice sub-step (App-installation or PAT secret); existing GitHub tab cleanup | Vitest + Playwright cover the new page; wizard reaches Install step in both auth-choice branches | B + C |
| I | `spring-voyage-oss` package: install flow continues to use App-installation by default; no ADR-0034 amendment | Package install e2e green against a repo with the SV App installed and against a repo without (PAT path through the binding) | D + E |
| J | Documentation: `concepts/humans.md`, `concepts/tenants.md`, `architecture/identifiers.md`; amendment line on ADR-0036 §1 (new actor kind) | Docs render cleanly; docs-evergreen-framing CI job passes; CodeQL / openapi-drift clean | D + H |

Filing-time refinement: any phase whose natural PR count exceeds one fans out into ordered sub-issues at the obvious seam. The expected fan-out is roughly A → 2 PRs (entity + migration; `UnitGitHubConfig` shape change), B → 2 PRs (routes + 410 stub; OpenAPI regen), G → 2 PRs (CLI rename; binding flag changes), H → 2 PRs (page + wizard); the rest are sized for a single PR.

## Phase narratives

### Phase A — Domain model + EF migration

Lands the foundation: the new `TenantUserEntity`, the `TenantUserConnectorIdentities` table (rekeyed and shrunk from `HumanConnectorIdentities`), the `OssTenantUserIds.Operator` constant (decision 3 of ADR-0047), and the `UnitGitHubConfig` record shape change (drop `Owner`, add `PatSecretName`; keep `AppInstallationId`).

Files touched: `src/Cvoya.Spring.Core/Tenancy/OssTenantUserIds.cs` (new), `src/Cvoya.Spring.Dapr/Data/Entities/TenantUserEntity.cs` (new), `src/Cvoya.Spring.Dapr/Data/Entities/TenantUserConnectorIdentityEntity.cs` (new — replaces `HumanConnectorIdentityEntity.cs`, with the narrow shape: `username`, `display_handle?`, no `config_json`, no auth fields), `src/Cvoya.Spring.Dapr/Data/Configuration/*` (entity + index configuration), `src/Cvoya.Spring.Dapr/Migrations/<timestamp>_TenantUserAndConnectorIdentityRekey.cs` (drop old table; create new; alter `UnitGitHubConfig` columns), `src/Cvoya.Spring.Connector.GitHub/UnitGitHubConfig.cs` (record: drop `Owner`, add `PatSecretName`; keep `AppInstallationId`), `src/Cvoya.Spring.Core/Security/ITenantUserConnectorIdentityResolver.cs` (new — replaces `IHumanConnectorIdentityResolver.cs`), `src/Cvoya.Spring.Core/Identity/TenantUserKind.cs` (actor-kind enumeration addition).

The natural key on `TenantUserEntity` is `(tenant_id, auth_subject)` per ADR-0047 §1; `auth_subject` is nullable in OSS dev so the operator row is identified by its deterministic UUID.

Acceptance signal: build clean; `dotnet test` green; the migration applies cleanly against a non-empty starting state in a test DB; `OssTenantUserIds.Operator` value pinned with both dashed and no-dash literals; the actor-kind audit referenced in ADR-0047 § "Costs" lands as a same-PR sweep (Address parser, display-name validator, activity-log renderer).

The amendment line on ADR-0036 §1 (the actor-kind enumeration adds `tenant-user`) lands here, in the same PR as the enum addition.

### Phase B — API surface

Relocates connector-identity endpoints under `/api/v1/tenant/users/{tenantUserId}/identities` (display-identity only — `username`, `display_handle?`), and retires the old `/api/v1/tenant/humans/{humanId}/identities` routes with a 410 stub (ADR-0047 §14). `UnitGitHubConfig{Request,Response}` drop `owner` and gain `pat_secret_name`; binding-create / -update endpoints enforce the "exactly one of `app_installation_id` or `pat_secret_name`" rule with `GitHubBindingAuthRequired` / `GitHubBindingAuthAmbiguous` error codes. OpenAPI / Kiota / `openapi-typescript` regen lands in the same phase.

There is **no** sibling `/config/{connectorSlug}` route group — user identity is display-only and the `/identities` routes cover the full surface.

Files touched: `src/Cvoya.Spring.Host.Api/Endpoints/TenantUserIdentityEndpoints.cs` (new — replaces `HumanIdentityEndpoints.cs`; display-identity DTOs only), `src/Cvoya.Spring.Host.Api/Endpoints/RetiredHumanIdentityEndpoints.cs` (new — 410 stub), `src/Cvoya.Spring.Host.Api/Endpoints/UnitConnectorBindingEndpoints.cs` (binding-auth gate + DTO shape change), `src/Cvoya.Spring.Host.Api/Models/*` (request / response DTO renames + shape changes), `openapi.json`, generated Kiota client files, generated `openapi-typescript` types.

Acceptance signal: integration tests pin happy-path and conflict / 404 cases for every relocated route; the 410 stub test asserts the structured migration-hint body; the binding-auth gate test asserts neither/both rejections; `/openapi-diff` clean.

### Phase C — Connector contract extension

Extends `IConnectorType` so each connector contributes both a unit-binding config schema (today) and a user-config schema (new, display-identity only). The GitHub connector contributes the `GitHubUserConfig` schema (`{ username, display_handle? }`). Connectors without a display-identity concept return an empty schema and render as "no per-user configuration."

Files touched: `src/Cvoya.Spring.Connectors.Abstractions/IConnectorType.cs` (contract extension), `src/Cvoya.Spring.Connector.GitHub/GitHubUserConfig.cs` (new record — display fields only), `src/Cvoya.Spring.Connector.GitHub/GitHubConnectorType.cs` (schema contribution), `tests/unit/Cvoya.Spring.Connectors.Tests/*` (contract test + GitHub-fixture test).

Acceptance signal: build clean; the schema-contribution test resolves a non-empty user-config schema from the GitHub connector and an empty one from a no-op fixture connector; existing unit-binding-schema tests still pass against the extended contract.

### Phase D — GitHub connector auth resolution

Lands the binding-read dispatch (ADR-0047 §6): read the unit binding, dispatch to the App-installation branch if `AppInstallationId` is set, dispatch to the PAT branch if `PatSecretName` is set. The binding-create gate (Phase B) is the structural guarantee that exactly one is set; the connector treats the "neither" case as a defensive assertion (logged + raised as `GitHubBindingAuthMissing`). PAT secrets read through `ISecretResolver` so the Unit → Tenant fall-through (ADR-0003) applies automatically.

There is **no auth chain**. There is no "calling tenant user" lookup. Every outbound call from the unit uses the binding's pinned credential.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Auth/GitHubBindingAuthResolver.cs` (new — the single dispatch), `src/Cvoya.Spring.Connector.GitHub/Auth/GitHubAuthErrors.cs` (new — single structured error: `GitHubBindingAuthMissing`), the connector's outbound-write call sites (Octokit factories, label-roundtrip subscriber, the PR-files fetcher per #2385), unit + integration tests under `tests/unit/Cvoya.Spring.Connector.GitHub.Tests/Auth/`.

Acceptance signal: unit tests cover both branches (App-installation token mint, PAT resolved through `ISecretResolver`); integration tests pin one App-resolved write and one PAT-resolved write end-to-end; existing GitHub-write call sites' tests stay green after the resolver swap.

### Phase E — Webhook handler simplification + fan-out within tenant

Drops `installation_id` from the webhook resolution path; `(owner, repo)` within the receiving tenant is the routing key. Many bindings per `(tenant, owner, repo)` is supported: the matcher returns every binding in the tenant whose `(owner, repo)` matches; per-binding filters (`include_labels`, `exclude_labels`, `include_authors`, `include_paths`) decide which units process the event. Adds binding-create-time `GitHubCrossTenantRepoBindingConflict` rejection (two tenants attempting the same `(owner, repo)` → rejected; cross-tenant only). UC4 (local-dev `gh webhook forward`) works without any matcher branching.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Webhooks/GitHubWebhookHandler.cs` (the matcher — fan-out within tenant), `src/Cvoya.Spring.Core/Connectors/IUnitConnectorBindingLookup.cs` (signature for multi-binding return), `src/Cvoya.Spring.Host.Api/Endpoints/UnitConnectorBindingEndpoints.cs` (cross-tenant conflict rejection), unit + integration tests.

Acceptance signal: gh-webhook-forward e2e green (the original #2487 reproduction case); cross-tenant binding-create rejection test green; in-tenant fan-out test (`frontend-team` + `backend-team` bindings on the same monorepo, divergent label filters, both receive event, only the filter-matching one processes) green.

### Phase F — OAuth wiring (UC8)

Wires the existing `Auth/OAuth/` modules (`GitHubOAuthService`, `GitHubOAuthEndpoints`, session / state stores, user fetcher, scope resolver, repo filter) into the new flows. The OAuth callback persists the resulting token to the tenant secret store under the binding-scoped naming convention from ADR-0047 §5. When the flow was initiated from the binding-create wizard, the callback returns a payload the wizard uses to pre-fill `pat_secret_name`. Optionally, when initiated from the user-identity surface, the OAuth user-info response populates the calling `TenantUser`'s GitHub `username`.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/GitHubOAuthEndpoints.cs` (wiring; token-persist hook), `src/Cvoya.Spring.Host.Api/Endpoints/TenantUserIdentityEndpoints.cs` (optional `username` update on OAuth completion from the identity surface), `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/OAuthTokenPersister.cs` (new — writes secret + returns the secret name), tests.

Acceptance signal: e2e test exercises "Authorize with GitHub" round-trip via portal — landing page → OAuth start → callback → secret written → binding-create wizard pre-filled with the secret name → subsequent outbound write resolves through the PAT branch of decision 6. Manual-paste path retains its dedicated test.

### Phase G — CLI

Renames `spring human identity {set,list,remove}` to `spring user identity {set,list,remove}` (no shim). Binding subcommands gain `--pat-secret-name <name>`; the binding-create command rejects at parse time if neither `--installation-id` nor `--pat-secret-name` is supplied, or if both are. `--owner` is removed; `--repo` accepts `owner/repo` only.

There is **no** `spring user config <connector>` verb namespace — `spring user identity` covers the entire user-side surface.

Files touched: `src/Cvoya.Spring.Cli/Commands/UserCommand.cs` (new — replaces `HumanCommand.cs` for the identity verbs), `src/Cvoya.Spring.Cli/Commands/UnitCommand.cs` (binding subcommands — drop `--owner`; add `--pat-secret-name`; auth-choice gate), CLI scenario tests under `tests/e2e/cli/`.

Acceptance signal: CLI scenarios cover the new verbs' happy paths; the old verbs and dropped flags fail at parse time with structured hints; binding-create rejects neither/both auth-flag combinations at parse time; help text clean.

### Phase H — Portal

The user-identity page is **display-only**: per-connector sub-surfaces accept `username` and optional `display_handle` (driven by each connector's user-config schema from ADR-0047 §4). No PAT input on this page. The new-unit wizard's GitHub step adds an **auth-choice sub-step** with two branches: "Use an App installation" (existing flow — installation id) or "Use a PAT secret" (new flow — operator authorizes via OAuth or pastes a token; the wizard persists the secret and uses the secret name). The existing GitHub connector tab loses the owner field; `repo` becomes `owner/repo`.

Files touched: `src/Cvoya.Spring.Web/src/app/settings/user-identity/page.tsx` (new — display-identity only), `src/Cvoya.Spring.Web/src/components/user-identity/connector-card.tsx` (new — schema-driven), `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` (wizard auth-choice sub-step), `src/Cvoya.Spring.Web/src/components/connectors/github-tab.tsx` (owner field removal).

Acceptance signal: vitest covers the schema-driven sub-surface and the wizard's two auth-choice branches; Playwright e2e covers an end-to-end "authorize → wizard pre-fills `pat_secret_name` → bind unit → unit writes via PAT" scenario and a parallel "pick App installation → bind unit → unit writes via App" scenario.

### Phase I — `spring-voyage-oss` package

The OSS package continues to use the App-installation auth path by default — ADR-0034's atomic binding at template-apply time with `installation_id` remains valid. The PAT path is documented in the package README as the alternative for operators who want it, but the default install flow is unchanged. No ADR-0034 amendment line is required.

Files touched: `packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml` (no shape change required if `AppInstallationId` is unchanged; verify the binding declaration still validates against the new schema), `packages/spring-voyage-oss/README.md` (add a "Use a PAT instead" appendix pointing at the wizard's auth-choice step), `docs/concepts/spring-voyage-oss.md` (note the alternative).

Acceptance signal: package-install e2e green against a repo with the SV App installed (App branch wins) and a documented manual scenario for the PAT branch.

### Phase J — Documentation

Updates the concept and architecture docs to describe the `TenantUser` concept and the new identity layout. Adds the amendment line on ADR-0036 §1 (the actor-kind enumeration). Cross-links ADR-0047 throughout.

The ADR-0034 amendment is **dropped** from this plan — ADR-0034's §§ 4–5 are not obsoleted by ADR-0047 (the binding still atomically captures auth at template-apply time; the change is that auth may now be `pat_secret_name` as well as `installation_id`, which is an extension, not an obsolescence).

Files touched: `docs/concepts/humans.md` (the `Human → TenantUser` display mapping, the OSS default), `docs/concepts/tenants.md` (where `TenantUser` rows live in the model; cross-tenant identity is two rows), `docs/architecture/identifiers.md` (the actor-kind list, the `OssTenantUserIds.Operator` recipe), `docs/decisions/0036-single-identity-model.md` (the amendment line on §1 — landed alongside the Phase A actor-kind change but doc-finalised here).

Acceptance signal: docs render cleanly; the docs-evergreen-framing CI job passes; every concept doc that referenced "human identity" updates to point at the `TenantUser` surface.

## Out of scope

Restated verbatim from the umbrella body (#2487):

- **OUT1 — Multi-`TenantUser` OSS deployment.** v0.1 has exactly one `TenantUser` in OSS (the operator). Schema and surfaces must not preclude N, but multi-user OSS sign-in / admin lands later.
- **OUT2 — Per-human explicit mapping override.** v0.1 ships the default-to-operator behaviour. Explicit `human X ≡ tenant user Y` is hosted-mode work.
- **OUT3 — Tenant-multi-user authz** (per-Human admin roles, per-binding ACLs).

Additionally, out of scope for this initiative but explicitly noted:

- **Per-tenant connector user-config schema extension.** v0.1 closes the schema seam to what the connector contributes; tenant-level overrides of a connector's user-config schema are a v0.2 consideration.
- **A `human_to_tenant_user` explicit override table for OSS.** OSS uses the derived projection (every `Human` resolves to the operator). The explicit table lands when OUT2 is in scope.
- **Slack / Linear connector user-config schemas.** ADR-0047 §4 extends the seam; concrete schemas for non-GitHub connectors land with those connectors' own work.
- **Per-caller-`TenantUser` binding auth.** Explicitly rejected by ADR-0047 §6 / "Alternatives considered." If a real use case emerges, ADR-0047's "Revisit criteria" reopens the question.

## GitHub issue filing plan

After this PR lands and ADR-0047 is Accepted, file one sub-issue per phase (or per natural single-PR slice when a phase exceeds one PR) under umbrella #2487 with native `blockedBy` edges set via `gh-app`. Each sub-issue carries the `area:tenant-user-split` label (file the label first if it does not exist), type `Task` (or `Feature` for phases A, D, F, H), milestone `v0.1`, and a thin body — files, deliverable, acceptance criteria copied from the phase narrative, no architectural reasoning (the ADR holds the reasoning).

The umbrella issue body remains the authoritative scope statement; sub-issues are the unit of execution. The umbrella closes when its last sub-issue closes.
