# Agent Runtimes & Tenant Scoping

> Canonical architecture reference for the #674 refactor. Read this to understand how Spring Voyage models tenant-scoped data, plugin-registered agent runtimes and connectors, and the operator-facing install / credential-health surface — without having to page through every phase issue.

## Overview

the platform is **tenant-scoped end-to-end** and turns AI "providers + execution tools" into a single unified **Agent Runtime** plugin concept, parallel to the existing **Connector** plugin. Every business-data row carries a `tenant_id`; every plugin registers in DI and becomes _available_ to the host; per-tenant install tables decide which plugins are _visible_ to a given tenant's workflows. A shared credential-health store tracks whether each tenant's stored credentials currently work.

The OSS core ships single-tenant at runtime — every fresh-install tenant-scoped row is owned by `Cvoya.Spring.Core.Tenancy.OssTenantIds.Default`, the deterministic v5 UUID materialised by a first-start bootstrap. The private cloud repo swaps in a request-scoped `ITenantContext` to turn the same schema into a multi-tenant deployment without forking. See [Identifiers § 5](identifiers.md#5-the-oss-default-tenant-id) for the constant and its derivation.

## Tenant scoping model

Every business-data entity implements `Cvoya.Spring.Core.Tenancy.ITenantScopedEntity`. The contract is one column — `Guid TenantId { get; }` — plus a convention that every `IEntityTypeConfiguration<T>` pairs the entity with a query filter:

```csharp
modelBuilder.Entity<SomeEntity>()
    .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
```

The filter lives on `SpringDbContext.OnModelCreating` rather than the per-entity config so the closure captures `this.CurrentTenantId` — EF Core re-evaluates the filter against each context instance at query time, giving every request its own tenant view from a shared model cache.

**Auto-populate on insert.** `SpringDbContext.ApplyAuditTimestamps` writes `TenantId` from the ambient `ITenantContext` when the caller leaves it unset, so individual write sites don't have to plumb tenant ids through every code path.

**Legitimate cross-tenant reads.** `DatabaseMigrator`, the default-tenant bootstrap, and a few platform-wide operational queries need to ignore the filter. They do so via `ITenantScopeBypass.BeginBypass(reason)`, which opens an audited scope (structured open + close logs, caller context, duration) and is swappable by the cloud repo for a permission-checked variant. **Never call `IgnoreQueryFilters()` directly in business code.**

**System / ops tables stay global.** Migrations history, startup-config evaluation state, anything the operator — not the customer — touches, does NOT implement `ITenantScopedEntity`.

**Bootstrap.** `DefaultTenantBootstrapService` (registered only in the Worker host, never in Host.Api, to avoid double-bootstrap races) iterates every registered `ITenantSeedProvider` on host startup inside a `BeginBypass` scope. Providers **must be idempotent** and **must not overwrite operator edits**. See `CONVENTIONS.md` § 13.

## Agent-runtime plugin model

```
      +------------------+
      | IAgentRuntime    |  <-- contract in Cvoya.Spring.Core/AgentRuntimes/
      +------------------+
              ^
      |       |       |       |
   Claude  OpenAI  Google  Ollama   <-- one project each under src/Cvoya.Spring.AgentRuntimes.<Name>/
      |       |       |       |
      +-------+-------+-------+
                      |  registered via each project's AddCvoyaSpringAgentRuntime<Name>() extension
                      v
              +-----------------------+
              | IAgentRuntimeRegistry |  <-- DI-resolved singleton in Cvoya.Spring.Dapr/AgentRuntimes
              +-----------------------+
                      |
                      v  enumerated by...
    +-----------------------------------------+
    |  tenant_agent_runtime_installs          |  <-- one row per (tenant, runtime) pair
    |  (ITenantAgentRuntimeInstallService)    |
    +-----------------------------------------+
                      |
                      v  consumed by...
              +-------------+
              | Unit / API  |   (wizard reads GET /api/v1/agent-runtimes/{id}/models)
              +-------------+
```

**`IAgentRuntime`** bundles:
- `Id` (stable, e.g. `claude`), `DisplayName`, `ToolKind` (`claude-code-cli`, `dapr-agent`, …).
- `CredentialSchema` — what credential the runtime expects.
- `CredentialSecretName` — canonical secret-store key (stable; persisted).
- `DefaultModels` — seed catalog loaded from `agent-runtimes/<id>/seed.json`.
- `GetProbeSteps(config, credential)` — declarative plan of in-container probe commands the `UnitValidationWorkflow` executes inside the unit's chosen image (see [Unit validation workflow](units.md#unit-validation-workflow)). Host-side shelling out was retired in #941.
- `FetchLiveModelsAsync(credential, ct)` — best-effort catalog refresh from the provider's live API.

**`IAgentRuntimeRegistry`** is the DI singleton every API layer / wizard / CLI consumes. Lookups are case-insensitive on `Id`.

**Per-tenant installs.** `tenant_agent_runtime_installs (tenant_id, runtime_id, config_json, installed_at, updated_at)` — one row per (tenant, runtime) pair; `config_json` stores `{ Models, DefaultModel, BaseUrl }`. The `ITenantAgentRuntimeInstallService` exposes `Install / Uninstall / List / Get / UpdateConfig`; `AgentRuntimeInstallSeedProvider` (priority 20) auto-installs every registered runtime onto the default tenant at bootstrap.

**HTTP surface.** `/api/v1/agent-runtimes/…` — `GET /` (tenant list), `GET /{id}`, `GET /{id}/models`, `POST /{id}/install`, `DELETE /{id}`, `PATCH /{id}/config`, `GET /{id}/credential-health`, `POST /{id}/refresh-models`. Every route requires auth. (The host-side `POST /{id}/validate-credential` and `POST /{id}/verify-baseline` endpoints were removed in #941; unit-scoped validation runs in-container via `UnitValidationWorkflow` — see [`units.md`](units.md#unit-validation-workflow).)

**CLI surface.** `spring agent-runtime list / show / install / uninstall / models list/set/add/remove / config set / credentials status / refresh-models`. CLI-only admin surface per the #674 carve-out — the portal may render read-only banners but mutation goes through the CLI. Unit-level validation is exposed via the unit lifecycle: `spring unit create` (default `--wait`) and `spring unit revalidate <name>`.

## Connector plugin model

Connectors existed before this design (`IConnectorType` in `Cvoya.Spring.Connectors.Abstractions`). The refactor adds:

- **Per-tenant install table** — `tenant_connector_installs (tenant_id, connector_id, config_json, installed_at, updated_at)`. `connector_id` is the connector `Slug`. `ConnectorInstallConfig` wraps an opaque `JsonElement?` because each connector's tenant-level config shape is its own concern.
- **Credential hooks on `IConnectorType`** — optional default-`null` `ValidateCredentialAsync` + `VerifyContainerBaselineAsync` overrides. Connectors that don't carry auth (Arxiv, WebSearch) inherit the no-op; connectors that do (GitHub) override.
- **HTTP surface** — `GET /api/v1/connectors` already returns the tenant-installed list (the same data that backs the portal's connector chooser); `GET /{slugOrId}`, `POST /{slugOrId}/install`, `DELETE /{slugOrId}/install`, `PATCH /{slugOrId}/install/config`, `POST /{slugOrId}/validate-credential`, `GET /{slugOrId}/credential-health`. There is no separate `/installed` collection — the install scope is the catalog. The full registered superset is no longer surfaced over HTTP; inspect the DI registry from a debug session if you need it.
- **CLI surface** — `spring connector list / show / install / uninstall / credentials status` (tenant install) alongside the existing per-unit `catalog / unit-binding / bind / bindings` verbs. `catalog` is currently a synonym of `list` (tenant-installed only) and shares the portal's chooser data.

## Credential-health lifecycle

> **Backend-side validation landed in #941.** The accept-time host-side probe for agent runtimes is gone. Per-unit credential checks now run inside the chosen container image as part of `UnitValidationWorkflow`. Connectors continue to use accept-time validation (no container contract for them yet). The credential-health store below is now fed by the watchdog path only — see [`units.md` → Unit validation workflow](units.md#unit-validation-workflow) for the in-container probe flow.

```
use-time (all subjects)                    in-container (units only)
------------------------                   ----------------------------
HttpClient(some-runtime) -> backend        UnitValidationWorkflow
         |                                 (PullingImage → VerifyingTool
         v   (on 401/403)                   → ValidatingCredential
DelegatingHandler inspects                  → ResolvingModel)
response status                                   |
         |                                        v
         v                             writes UnitDefinition.LastValidationError
CredentialHealthWatchdogHandler           (structured, redacted)
         |
         v
+--------------------------------+
| credential_health              |   <-- PK (tenant_id, kind, subject_id, secret_name)
| (ICredentialHealthStore)       |       read by operator via GET /credential-health
+--------------------------------+       and `spring … credentials status`
```

`CredentialHealthStatus` is a persistent state machine (`Unknown | Valid | Invalid | Expired | Revoked`). The watchdog handler writes on auth failures (`401 → Invalid`, `403 → Revoked`) so a flaky upstream doesn't flap the operator-facing status. Runtimes and connectors opt into the watchdog by calling `.AddCredentialHealthWatchdog(kind, subjectId, secretName)` on their `IHttpClientBuilder` — see `CONVENTIONS.md` § 16. For the in-container side, the unit's `LastValidationError` (surfaced on `GET /api/v1/units/{name}`) is authoritative for the most recent validation run, and the `/revalidate` endpoint re-dispatches the workflow after an operator-side fix.

## Admin surface policy

Every admin/operator mutation is **CLI-only**. The portal MAY expose read-only views for visibility but does not mutate:

- Agent-runtime install/config → `spring agent-runtime …`
- Connector install/config → `spring connector …`
- Unit validation → `spring unit create` (default `--wait`) / `spring unit revalidate <name>`
- Credential health → `spring … credentials status` (reads only; writes come from the watchdog + the unit-scoped `UnitValidationWorkflow`)
- Tenant seeds → Worker bootstrap (no HTTP / CLI re-seed.)
- Skill-bundle bindings → Worker bootstrap.; `spring skill-bundle …` mutation CLI deferred to a future release

This is ADDITIVE to `CONVENTIONS.md` § 14 (UI / CLI parity for user-facing features). See `AGENTS.md` § "Admin surfaces (CLI-only)" for the canonical version.

## Adding a new agent runtime

1. Create `src/Cvoya.Spring.AgentRuntimes.<Name>/` (e.g. `Cvoya.Spring.AgentRuntimes.Foo`). Reference `Cvoya.Spring.Core` only — no Dapr, no ASP.NET.
2. Implement `IAgentRuntime`. Pick a stable lower-case `Id`; pick a `ToolKind` (reuse `claude-code-cli` / `dapr-agent` / `codex-cli` where it fits).
3. Ship a `seed.json` at `agent-runtimes/<id>/seed.json` carrying the default model catalog.
4. Implement `GetProbeSteps(config, credential)` returning an ordered in-container probe plan — typically `VerifyingTool`, `ValidatingCredential`, `ResolvingModel` (omit `ValidatingCredential` when `CredentialKind.None`). Each step must have a bounded `Timeout` and an `InterpretOutput` delegate that never leaks the raw credential into the returned `UnitValidationError`. Do **not** emit `PullingImage` — the dispatcher owns that step.
5. Add `AddCvoyaSpringAgentRuntime<Name>()` DI extension that registers via `TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntime, FooRuntime>())` so cloud overlays can pre-register variants.
6. If the runtime authenticates via `HttpClient`, wire `.AddCredentialHealthWatchdog(CredentialHealthKind.AgentRuntime, "<id>", "api-key")` on the named HttpClient builder.
7. Ship a per-project `README.md` documenting id, tool kind, credential schema, and the runtime-image contract (which binaries the probe plan needs — typically `curl`).
8. Append a row to the "Built-in agent runtimes" table in `AGENTS.md` and register `AddCvoyaSpringAgentRuntime<Name>()` from `src/Cvoya.Spring.Host.Api/Program.cs`.

Bootstrap picks it up automatically: `AgentRuntimeInstallSeedProvider` enumerates the registry on every Worker start and calls `InstallAsync(id, config: null, …)` per runtime.

## Adding a new connector

Parallel to the above:

1. Create `src/Cvoya.Spring.Connector.<Name>/`. Reference `Cvoya.Spring.Connectors.Abstractions` (which transitively pulls `Cvoya.Spring.Core` + `Microsoft.AspNetCore.App`).
2. Implement `IConnectorType`. Pick a stable `Slug` + `Guid TypeId` (persisted across renames).
3. Implement typed HTTP routes via `MapRoutes(IEndpointRouteBuilder group)` — the host pre-scopes the group to `/api/v1/connectors/{slug}` so your implementation maps relative routes (`units/{unitId}/config`, `actions/{name}`, `config-schema`) and stays ignorant of the outer path.
4. Override `ValidateCredentialAsync` / `VerifyContainerBaselineAsync` if the connector carries auth or needs host-side tooling. Both default to no-op.
5. Add `AddCvoyaSpringConnector<Name>(IConfiguration)` DI extension and register in `Program.cs`.
6. If the connector authenticates via `HttpClient`, wire `.AddCredentialHealthWatchdog(CredentialHealthKind.Connector, "<slug>", "<secret-name>")`.

`ConnectorInstallSeedProvider` (priority 30) auto-installs every registered connector on bootstrap, the same way runtimes are seeded.

## Further reading

- `AGENTS.md` § Open-Source Platform & Extensibility — extension-model rules (TryAdd*, no-seal, virtual hooks).
- `CONVENTIONS.md` § 13 (tenant scoping), § 15 (skill-bundle binding), § 16 (credential-health watchdog), § 17 (plugin contracts).
- Tracker issue [#674](https://github.com/cvoya-com/spring-voyage/issues/674) — the phased roadmap with per-sub-issue acceptance criteria.
