# Agent Runtimes & Tenant Scoping

> Canonical architecture reference for the #674 refactor. Read this to understand how Spring Voyage V2 models tenant-scoped data, plugin-registered agent runtimes and connectors, and the operator-facing install / credential-health surface — without having to page through every phase issue.

## Overview

V2 makes the platform **tenant-scoped end-to-end** and turns AI "providers + execution tools" into a single unified **Agent Runtime** plugin concept, parallel to the existing **Connector** plugin. Every business-data row carries a `tenant_id`; every plugin registers in DI and becomes _available_ to the host; per-tenant install tables decide which plugins are _visible_ to a given tenant's workflows. A shared credential-health store tracks whether each tenant's stored credentials currently work.

The OSS core ships single-tenant at runtime (the literal `"default"` tenant is materialised by a first-start bootstrap). The private cloud repo swaps in a request-scoped `ITenantContext` to turn the same schema into a multi-tenant deployment without forking.

## Tenant scoping model

Every business-data entity implements `Cvoya.Spring.Core.Tenancy.ITenantScopedEntity`. The contract is one column — `string TenantId { get; }` — plus a convention that every `IEntityTypeConfiguration<T>` pairs the entity with a query filter:

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
- `ValidateCredentialAsync(credential, ct)` — used by accept-time validation and the credential-health watchdog.
- `DefaultModels` — seed catalog loaded from `agent-runtimes/<id>/seed.json`.
- `VerifyContainerBaselineAsync(ct)` — probes whether the runtime's required host-side tooling is present (CLI binary, reachable sidecar, …).

**`IAgentRuntimeRegistry`** is the DI singleton every API layer / wizard / CLI consumes. Lookups are case-insensitive on `Id`.

**Per-tenant installs.** `tenant_agent_runtime_installs (tenant_id, runtime_id, config_json, installed_at, updated_at)` — one row per (tenant, runtime) pair; `config_json` stores `{ Models, DefaultModel, BaseUrl }`. The `ITenantAgentRuntimeInstallService` exposes `Install / Uninstall / List / Get / UpdateConfig`; `AgentRuntimeInstallSeedProvider` (priority 20) auto-installs every registered runtime onto the default tenant at bootstrap.

**HTTP surface.** `/api/v1/agent-runtimes/…` — `GET /` (tenant list), `GET /{id}`, `GET /{id}/models`, `POST /{id}/install`, `DELETE /{id}`, `PATCH /{id}/config`, `POST /{id}/validate-credential`, `GET /{id}/credential-health`, `POST /{id}/verify-baseline`. Every route requires auth.

**CLI surface.** `spring agent-runtime list / show / install / uninstall / models list/set/add/remove / config set / credentials status / refresh-models`. CLI-only admin surface per the #674 carve-out — the portal may render read-only banners but mutation goes through the CLI. Container-baseline verification is HTTP-only today (`POST /api/v1/agent-runtimes/{id}/verify-baseline`); a `spring agent-runtime verify-baseline` verb is a tracked follow-up.

## Connector plugin model

Connectors existed before V2 (`IConnectorType` in `Cvoya.Spring.Connectors.Abstractions`). The refactor adds:

- **Per-tenant install table** — `tenant_connector_installs (tenant_id, connector_id, config_json, installed_at, updated_at)`. `connector_id` is the connector `Slug`. `ConnectorInstallConfig` wraps an opaque `JsonElement?` because each connector's tenant-level config shape is its own concern.
- **Credential hooks on `IConnectorType`** — optional default-`null` `ValidateCredentialAsync` + `VerifyContainerBaselineAsync` overrides. Connectors that don't carry auth (Arxiv, WebSearch) inherit the no-op; connectors that do (GitHub) override.
- **HTTP surface** — `GET /api/v1/connectors` already returns the tenant-installed list (the same data that backs the portal's connector chooser); `GET /{slugOrId}`, `POST /{slugOrId}/install`, `DELETE /{slugOrId}/install`, `PATCH /{slugOrId}/install/config`, `POST /{slugOrId}/validate-credential`, `GET /{slugOrId}/credential-health`. There is no separate `/installed` collection — the install scope is the catalog. The full registered superset is no longer surfaced over HTTP; inspect the DI registry from a debug session if you need it.
- **CLI surface** — `spring connector list / show / install / uninstall / credentials status` (tenant install) alongside the existing per-unit `catalog / unit-binding / bind / bindings` verbs. `catalog` is currently a synonym of `list` (tenant-installed only) and shares the portal's chooser data.

## Credential-health lifecycle

> **In-flight rework — [#941](https://github.com/cvoya-com/spring-voyage/issues/941).** The accept-time half of this lifecycle moves onto the dispatcher and runs validation **inside the chosen container image**, behind a new `Validating` unit status with four probe steps (`PullingImage` → `VerifyingTool` → `ValidatingCredential` → `ResolvingModel`). `IRuntimeProbeActor` and `RuntimeProbeActor` replace the host-side path; `UnitCreationService` is refactored to dispatch validation rather than run it inline; a `/units/{name}/revalidate` endpoint and `spring unit revalidate` verb provide the retry surface. The use-time watchdog described below is unaffected. When #941 lands, the diagram's `RecordAsync` writer for the accept-time path becomes the new actor; the credential-health table shape stays compatible.


```
accept-time                                           use-time
-----------                                           --------
POST /…/validate-credential                           HttpClient(some-runtime) --> backend
         |                                                |
         v                                                v   (on 401/403)
ValidateCredentialAsync ----,                     DelegatingHandler inspects
         |                  |                        response status
         v                  |                              |
CredentialValidationResult  |                              v
         |                  |             CredentialHealthWatchdogHandler
         v                  |                              |
RecordAsync(status, error)--+------------------------------+
         |
         v
+--------------------------------+
| credential_health              |   <-- PK (tenant_id, kind, subject_id, secret_name)
| (ICredentialHealthStore)       |       read by operator via GET /credential-health
+--------------------------------+       and `spring … credentials status`
```

`CredentialHealthStatus` is a persistent state machine (`Unknown | Valid | Invalid | Expired | Revoked`) distinct from the per-attempt `CredentialValidationStatus`. Accept-time validation writes the full status (success flips `Valid`, 401 flips `Invalid`); the watchdog handler writes only on auth failures (`401 → Invalid`, `403 → Revoked`) so a flaky upstream doesn't flap the operator-facing status. Runtimes and connectors opt into the watchdog by calling `.AddCredentialHealthWatchdog(kind, subjectId, secretName)` on their `IHttpClientBuilder` — see `CONVENTIONS.md` § 16.

## Admin surface policy

Every admin/operator mutation is **CLI-only**. The portal MAY expose read-only views for visibility but does not mutate:

- Agent-runtime install/config → `spring agent-runtime …`
- Connector install/config → `spring connector …`
- Credential health → `spring … credentials status` (reads only; writes come from accept-time validation + watchdog)
- Tenant seeds → Worker bootstrap (no HTTP / CLI re-seed in V2)
- Skill-bundle bindings → Worker bootstrap in V2; `spring skill-bundle …` mutation CLI deferred to V2.1

This is ADDITIVE to `CONVENTIONS.md` § 14 (UI / CLI parity for user-facing features). See `AGENTS.md` § "Admin surfaces (CLI-only)" for the canonical version.

## Adding a new agent runtime

1. Create `src/Cvoya.Spring.AgentRuntimes.<Name>/` (e.g. `Cvoya.Spring.AgentRuntimes.Foo`). Reference `Cvoya.Spring.Core` only — no Dapr, no ASP.NET.
2. Implement `IAgentRuntime`. Pick a stable lower-case `Id`; pick a `ToolKind` (reuse `claude-code-cli` / `dapr-agent` / `codex-cli` where it fits).
3. Ship a `seed.json` at `agent-runtimes/<id>/seed.json` carrying the default model catalog.
4. Add `AddCvoyaSpringAgentRuntime<Name>()` DI extension that registers via `TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntime, FooRuntime>())` so cloud overlays can pre-register variants.
5. If the runtime authenticates via `HttpClient`, wire `.AddCredentialHealthWatchdog(CredentialHealthKind.AgentRuntime, "<id>", "api-key")` on the named HttpClient builder.
6. Ship a per-project `README.md` documenting id, tool kind, credential schema, and host-side baseline.
7. Append a row to the "Built-in agent runtimes" table in `AGENTS.md` and register `AddCvoyaSpringAgentRuntime<Name>()` from `src/Cvoya.Spring.Host.Api/Program.cs`.

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
