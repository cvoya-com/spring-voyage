# Platform-user / human split + connector identity location — execution plan

**Initiative.** Implementation of [ADR-0047 — Platform-user / human split; connector identity on the platform user](../../decisions/0047-platform-user-human-split.md). Tracked under the umbrella [#2487](https://github.com/cvoya-com/spring-voyage/issues/2487) on milestone `v0.1`. The umbrella supersedes the original single-PR shape of #2487 (a localised webhook fix) and pulls forward platform-user pieces originally scoped for v0.2 / v0.3.

This plan enumerates the **phase sequence by impact zone**, with the structural `blockedBy` ordering set at the phase grain. Per the umbrella's step-0 contract, individual issues are not pre-allocated here — each phase fans out into 1–3 single-PR sub-issues after this plan + ADR land and the ADR is moved to Accepted. The grain is "one phase, one or a handful of consecutive PRs."

## Conventions

These mirror the conventions established in [`docs/plan/v0.1/units-are-agents.md`](units-are-agents.md), the canonical v0.1 cross-cutting plan format. The summary form:

- **One task = one PR.** No batching, no "while we're here." If a phase exceeds one PR, the phase fans out into consecutive sub-issues at the obvious seam.
- **Acceptance criteria are testable.** Build clean, test green, file exists, function behaves — mechanically verifiable.
- **Tasks reference the ADR by section, not by paraphrase.** "See ADR-0047 §6" is enough; agents read the ADR rather than the paraphrase.
- **`blockedBy` is structural.** Set via GitHub's native blocked-by edges through `gh-app`, not stated in issue prose. The umbrella's sub-issue panel and each task's blocked-by panel surface the order; bodies stay thin.
- **Aggressive cleanup; no back-compat.** Deleted code's tests are deleted too. No shims, no `_legacyFoo` markers, no deprecation paths beyond what ADR-0047 §§ 8, 12, 14 list (the `HumanConnectorIdentities` table drop, the CLI rename, the 410 stub on the old API route).
- **Sub-issues file against the umbrella with `area:platform-user-split`.** Type `Task` (or `Feature` for the larger phases). Milestone `v0.1`.

## Phases at a glance

| Phase | Impact zone | Acceptance signal | Depends on |
|---|---|---|---|
| A | Domain model + EF migration: `PlatformUser`, `PlatformUserConnectorIdentity` rekey, `UnitGitHubConfig` shape change | Build + tests green; migration applies cleanly; `OssPlatformUserIds.Operator` constant pinned | — (foundation) |
| B | API surface: `/api/v1/tenant/users/*`; retire `/api/v1/tenant/humans/{id}/identities` with 410 stub; `UnitGitHubConfig{Request,Response}` shape change; OpenAPI regen | Integration tests pin every new route + retire stub; OpenAPI-diff clean | A |
| C | Connector contract extension: `IConnectorType` gains user-config schema seam; GitHub connector contributes `GitHubUserConfig` schema | Build clean; schema-contribution test covers GitHub and a no-op fixture connector | A |
| D | GitHub connector auth-resolution chain: App-installation → platform-user PAT → fail; structured errors `GitHubAuthUnavailable`, `GitHubAmbiguousInstallation` | Unit + integration tests cover each branch plus ambiguity / override paths; existing GitHub-write call sites green | C |
| E | Webhook handler simplification: `(owner, repo)` keying; drop `installation_id` branch; binding-create-time `GitHubRepoBindingConflict` rejection | UC4 e2e (gh-webhook-forward) green; cross-tenant binding-create rejection test green | D |
| F | OAuth wiring (UC8): existing `Auth/OAuth/` modules surfaced through user-config endpoints; OAuth-issued token persisted under the secret-name convention | E2E: portal "Authorize with GitHub" round-trip persists a secret + writes `pat_secret_name` to the user-config row | B + ADR-0003 (in place) |
| G | CLI: `spring user identity {set,list,remove}`; `spring user config github {get,set,authorize,...}`; binding-flow simplification (drop `--owner` / `--installation-id`) | CLI scenarios cover the new verbs + the dropped flags' parse-time rejections; help text clean | B |
| H | Portal: user-configuration page with per-connector sub-surfaces; new-unit wizard GitHub step simplification; GitHub tab cleanup | Vitest + Playwright cover the new page; wizard reaches Install step in the reduced step count | B + C |
| I | `spring-voyage-oss` package + ADR-0034 §§ 4–5 amendment line; install flow no longer collects `installation_id` | Package install e2e green against a repo with the SV App installed and against a repo without (PAT path) | D + E |
| J | Documentation: `concepts/humans.md`, `concepts/tenants.md`, `architecture/identifiers.md`; amendment lines on ADR-0036 and ADR-0034 | Docs render cleanly; docs-evergreen-framing CI job passes; CodeQL / openapi-drift clean | D + H |

Filing-time refinement: any phase whose natural PR count exceeds one fans out into ordered sub-issues at the obvious seam. The expected fan-out is roughly A → 2 PRs (entity + migration; `UnitGitHubConfig` shape change), B → 2 PRs (routes + 410 stub; OpenAPI regen), G → 2 PRs (CLI rename; user-config subcommands), H → 2 PRs (page + wizard); the rest are sized for a single PR.

## Phase narratives

### Phase A — Domain model + EF migration

Lands the foundation: the new `PlatformUserEntity`, the `PlatformUserConnectorIdentities` table (rekeyed from `HumanConnectorIdentities`), the `OssPlatformUserIds.Operator` constant (decision 3 of ADR-0047), and the `UnitGitHubConfig` record shape change (drop `Owner`, drop `AppInstallationId`).

Files touched: `src/Cvoya.Spring.Core/Tenancy/OssPlatformUserIds.cs` (new), `src/Cvoya.Spring.Dapr/Data/Entities/PlatformUserEntity.cs` (new), `src/Cvoya.Spring.Dapr/Data/Entities/PlatformUserConnectorIdentityEntity.cs` (new — replaces `HumanConnectorIdentityEntity.cs`), `src/Cvoya.Spring.Dapr/Data/Configuration/*` (entity + index configuration), `src/Cvoya.Spring.Dapr/Migrations/<timestamp>_PlatformUserAndConnectorIdentityRekey.cs` (drop old table; create new), `src/Cvoya.Spring.Connector.GitHub/UnitGitHubConfig.cs` (record shape change), `src/Cvoya.Spring.Core/Security/IPlatformUserConnectorIdentityResolver.cs` (new — replaces `IHumanConnectorIdentityResolver.cs`), `src/Cvoya.Spring.Core/Identity/PlatformUserKind.cs` (actor-kind enumeration addition).

Acceptance signal: build clean; `dotnet test` green; the migration applies cleanly against a non-empty starting state in a test DB; `OssPlatformUserIds.Operator` value pinned with both dashed and no-dash literals; the actor-kind audit referenced in ADR-0047 § "Costs" lands as a same-PR sweep (Address parser, display-name validator, activity-log renderer).

The amendment line on ADR-0036 §1 (the actor-kind enumeration adds `platform-user`) lands here, in the same PR as the enum addition.

### Phase B — API surface

Relocates connector-identity endpoints under `/api/v1/tenant/users/{platformUserId}/identities`, adds the user-config sibling group at `/api/v1/tenant/users/{platformUserId}/config/{connectorSlug}`, and retires the old routes with a 410 stub (ADR-0047 §14). `UnitGitHubConfig{Request,Response}` lose `owner` and `app_installation_id`. OpenAPI / Kiota / `openapi-typescript` regen lands in the same phase.

Files touched: `src/Cvoya.Spring.Host.Api/Endpoints/PlatformUserIdentityEndpoints.cs` (new — replaces `HumanIdentityEndpoints.cs`), `src/Cvoya.Spring.Host.Api/Endpoints/PlatformUserConfigEndpoints.cs` (new), `src/Cvoya.Spring.Host.Api/Endpoints/RetiredHumanIdentityEndpoints.cs` (new — 410 stub), `src/Cvoya.Spring.Host.Api/Models/*` (request / response DTO renames + shape changes), `openapi.json`, generated Kiota client files, generated `openapi-typescript` types.

Acceptance signal: integration tests pin happy-path and conflict / 404 cases for every relocated route; the 410 stub test asserts the structured migration-hint body; `/openapi-diff` clean.

### Phase C — Connector contract extension

Extends `IConnectorType` so each connector contributes both a unit-binding config schema (today) and a user-config schema (new). The GitHub connector contributes the `GitHubUserConfig` schema (`{ username, pat_secret_name?, app_installation_override? }`). Connectors without user-config concepts return an empty schema and render as "no per-user configuration."

Files touched: `src/Cvoya.Spring.Connectors.Abstractions/IConnectorType.cs` (contract extension), `src/Cvoya.Spring.Connector.GitHub/GitHubUserConfig.cs` (new record), `src/Cvoya.Spring.Connector.GitHub/GitHubConnectorType.cs` (schema contribution), `tests/unit/Cvoya.Spring.Connectors.Tests/*` (contract test + GitHub-fixture test).

Acceptance signal: build clean; the schema-contribution test resolves a non-empty user-config schema from the GitHub connector and an empty one from a no-op fixture connector; existing unit-binding-schema tests still pass against the extended contract.

### Phase D — GitHub connector auth-resolution chain

Lands the auth-resolution chain (ADR-0047 §6): App-installation → platform-user PAT → fail. Adds the structured-error vocabulary (`GitHubAuthUnavailable`, `GitHubAmbiguousInstallation`, `GitHubRepoBindingConflict`). Resolves the calling platform user from the agent's owning chain; in OSS this always lands on `OssPlatformUserIds.Operator`. PAT secrets read through `ISecretResolver` so the Unit → Tenant fall-through (ADR-0003) applies automatically.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Auth/GitHubAuthResolver.cs` (new — the chain), `src/Cvoya.Spring.Connector.GitHub/Auth/GitHubAuthErrors.cs` (new — structured errors), the connector's outbound-write call sites (Octokit factories, label-roundtrip subscriber, the PR-files fetcher per #2385), unit + integration tests under `tests/unit/Cvoya.Spring.Connector.GitHub.Tests/Auth/`.

Acceptance signal: unit tests cover each of the three branches plus ambiguity (multi-installation) and override (`app_installation_override` honoured) paths; integration tests pin one App-resolved write and one PAT-resolved write end-to-end; existing GitHub-write call sites' tests stay green after the resolver swap.

### Phase E — Webhook handler simplification

Drops `installation_id` from the webhook resolution path; `(owner, repo)` is the binding key. Adds binding-create-time `GitHubRepoBindingConflict` rejection (two tenants attempting the same `(owner, repo)` → rejected). UC4 (local-dev `gh webhook forward`) ends up working without any matcher branching.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Webhooks/GitHubWebhookHandler.cs` (the matcher), `src/Cvoya.Spring.Core/Connectors/IUnitConnectorBindingLookup.cs` (signature simplification if applicable), `src/Cvoya.Spring.Host.Api/Endpoints/UnitConnectorBindingEndpoints.cs` (binding-create rejection), unit + integration tests.

Acceptance signal: gh-webhook-forward e2e green (the original #2487 reproduction case); cross-tenant binding-create rejection test green; the matcher's prior two cases are visibly one in the diff.

### Phase F — OAuth wiring (UC8)

Wires the existing `Auth/OAuth/` modules (`GitHubOAuthService`, `GitHubOAuthEndpoints`, session / state stores, user fetcher, scope resolver, repo filter) into the new user-config endpoints. The OAuth callback persists the resulting token to the tenant secret store under the naming convention from ADR-0047 §5 and writes `pat_secret_name` to the `PlatformUserConnectorIdentity` row.

Files touched: `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/GitHubOAuthEndpoints.cs` (wiring into the user-config surface; final token-persist hook), `src/Cvoya.Spring.Host.Api/Endpoints/PlatformUserConfigEndpoints.cs` (extension), `src/Cvoya.Spring.Connector.GitHub/Auth/OAuth/OAuthTokenPersister.cs` (new — writes secret + user-config row in one transaction), tests.

Acceptance signal: e2e test exercises "Authorize with GitHub" round-trip via portal — landing page → OAuth start → callback → secret written → user-config row updated → subsequent outbound write resolves through step 2 of the auth chain. Manual-paste path retains its dedicated test.

### Phase G — CLI

Renames `spring human identity {set,list,remove}` to `spring user identity {set,list,remove}` (no shim). Adds `spring user config github {get,set,authorize,...}` (exact subcommands per CLI conventions). Drops `--owner` and `--installation-id` from the binding flows; both parse-time-rejected with a hint pointing at the new model.

Files touched: `src/Cvoya.Spring.Cli/Commands/UserCommand.cs` (new — replaces `HumanCommand.cs` for the identity verbs), `src/Cvoya.Spring.Cli/Commands/UnitCommand.cs` (binding subcommands — drop the flags), CLI scenario tests under `tests/e2e/cli/`.

Acceptance signal: CLI scenarios cover the new verbs' happy paths; the old verbs and dropped flags fail at parse time with structured hints; help text clean.

### Phase H — Portal

Adds a "User configuration" page under the operator's settings surface, with per-connector sub-surfaces driven by each connector's user-config schema (ADR-0047 §4). The new-unit wizard GitHub step simplifies to a single `owner/repo` field plus an auth-resolution preview ("SV App detected" / "BYO App `<client-id>` detected" / "Will use your PAT"). The existing GitHub connector tab loses the owner field.

Files touched: `src/Cvoya.Spring.Web/src/app/settings/user-config/page.tsx` (new), `src/Cvoya.Spring.Web/src/components/user-config/connector-card.tsx` (new — schema-driven), `src/Cvoya.Spring.Web/src/app/units/create/page.tsx` (wizard step simplification), `src/Cvoya.Spring.Web/src/components/connectors/github-tab.tsx` (owner field removal).

Acceptance signal: vitest covers the schema-driven sub-surface and the wizard step's three preview states; Playwright e2e covers an end-to-end "authorize → bind unit → unit writes via PAT" scenario.

### Phase I — `spring-voyage-oss` package + ADR-0034 amendment

Rewrites the OSS package's GitHub-binding flow so the operator no longer looks up `installation_id`. The unit-create path posts `(owner, repo)` only; auth resolves at use-time. The ADR-0034 §§ 4 and 5 amendment line lands in the same PR as the package rewrite — the unit's identity boundary is the operator's `PlatformUser`, not the per-unit App-installation binding.

Files touched: `packages/spring-voyage-oss/units/spring-voyage-oss/package.yaml` (binding declaration simplification), `packages/spring-voyage-oss/README.md` (operator playbook simplification), `docs/decisions/0034-oss-dogfooding-unit.md` (amendment line — see ADR-0047's "Amends" header for the exact form), `docs/concepts/spring-voyage-oss.md` (concept-level updates).

Acceptance signal: package-install e2e green against a repo with the SV App installed (step 1 of the auth chain wins) and against a repo without (step 2 wins via the operator's PAT). README walks the operator through both paths.

### Phase J — Documentation

Updates the concept and architecture docs to describe the platform-user concept and the new identity layout. Adds the amendment line on ADR-0036 §1 (the actor-kind enumeration). Cross-links ADR-0047 throughout.

Files touched: `docs/concepts/humans.md` (the human-to-platform-user mapping, the OSS default), `docs/concepts/tenants.md` (where platform users live in the model), `docs/architecture/identifiers.md` (the actor-kind list, the `OssPlatformUserIds.Operator` recipe), `docs/decisions/0036-single-identity-model.md` (the amendment line on §1 — landed alongside the Phase A actor-kind change but doc-finalised here), `docs/decisions/0034-oss-dogfooding-unit.md` (the amendment line landed in Phase I; this phase verifies the cross-references).

Acceptance signal: docs render cleanly; the docs-evergreen-framing CI job passes; every concept doc that referenced "human identity" updates to point at the platform-user surface.

## Out of scope

Restated verbatim from the umbrella body (#2487):

- **OUT1 — Multi-platform-user OSS deployment.** v0.1 has exactly one platform user (the operator). Schema and surfaces must not preclude N, but multi-user OSS sign-in / admin lands later.
- **OUT2 — Per-human explicit mapping override.** v0.1 ships the default-to-operator behaviour. Explicit `human X ≡ tenant user Y` is hosted-mode work.
- **OUT3 — Tenant-multi-user authz** (per-Human admin roles, per-binding ACLs).

Additionally, out of scope for this initiative but explicitly noted:

- **Per-tenant connector user-config schema extension.** v0.1 closes the schema seam to what the connector contributes; tenant-level overrides of a connector's user-config schema are a v0.2 consideration.
- **A `human_to_platform_user` explicit override table for OSS.** OSS uses the derived projection (every `Human` resolves to the operator). The explicit table lands when OUT2 is in scope.
- **Slack / Linear connector user-config schemas.** ADR-0047 §4 extends the seam; concrete schemas for non-GitHub connectors land with those connectors' own work.

## GitHub issue filing plan

After this PR lands and ADR-0047 is Accepted, file one sub-issue per phase (or per natural single-PR slice when a phase exceeds one PR) under umbrella #2487 with native `blockedBy` edges set via `gh-app`. Each sub-issue carries the `area:platform-user-split` label (file the label first if it does not exist), type `Task` (or `Feature` for phases A, D, F, H), milestone `v0.1`, and a thin body — files, deliverable, acceptance criteria copied from the phase narrative, no architectural reasoning (the ADR holds the reasoning).

The umbrella issue body remains the authoritative scope statement; sub-issues are the unit of execution. The umbrella closes when its last sub-issue closes.
