# 1629 — Bundled PR1+PR2+PR3 plan

## v5 UUID for OssTenantIds.Default

```
namespace = 00000000-0000-0000-0000-000000000000
label     = "cvoya/tenant/oss-default"
result    = dd55c4ea-8d72-5e43-a9df-88d07af02b69
no-dash   = dd55c4ea8d725e43a9df88d07af02b69
```

## Slice A: Core domain types

- `ITenantContext.CurrentTenantId : Guid`
- `ITenantScopedEntity.TenantId : Guid`
- `DirectoryEntry.ActorId : Guid` (rename to `Id`? leave as `ActorId : Guid` — rename later if needed)
- `Address(Scheme, Guid Id)` — single ctor
- `OssTenantIds.Default` (Guid), `DefaultDashed`, `DefaultNoDash` (string)
- `GuidFormatter.Format(Guid) -> "N"`, `TryParse(string, out Guid)` lenient
- `TenantRecord.Id : Guid` (Core record)
- `InstalledAgentRuntime.TenantId : Guid`
- `CredentialHealth.TenantId : Guid` + `SubjectId : Guid?` + `Scope` enum split
- `IAgentToolLauncher.TenantId : Guid` default `OssTenantIds.Default`
- `IAgentContextBuilder.TenantId : Guid` default `OssTenantIds.Default`
- `TenantSkillBundleBinding.TenantId : Guid`
- `ITenantSeedProvider.ApplySeedsAsync(Guid tenantId, ...)`

## Slice B: Entities + EntityConfigurations

- `TenantRecordEntity.Id : Guid`
- `UnitDefinitionEntity`: drop `UnitId` (slug), drop `ActorId` (merge to `Id`), drop `IsTopLevel`. Drop `Members` JsonElement (it's a slug snapshot). PK `(TenantId, Id)`.
- `AgentDefinitionEntity`: drop `AgentId` (slug), drop `ActorId`. `CreatedBy : Guid?` FK to humans.id.
- `UnitMembershipEntity`: shape `(TenantId Guid, UnitId Guid, AgentId Guid)`. (already mostly Guid). No slug.
- `UnitSubunitMembershipEntity`: `(TenantId Guid, ParentId Guid, ChildId Guid)`. PK triple. Drop slug.
- `HumanEntity`: TenantId Guid; Username slug stays.
- `ApiTokenEntity`: TenantId Guid; UserId Guid?
- `ConnectorDefinitionEntity`: drop ConnectorId slug. `Slug` removed entirely (cascade to PR4 callers).
- `SecretRegistryEntry`: TenantId Guid; OwnerId Guid?; Scope enum already there.
- `CredentialHealthEntity`: TenantId Guid; SubjectId Guid?; Scope split via Kind enum.
- `TenantConnectorInstallEntity`: TenantId Guid; keep ConnectorId (slug) per design — catalog content.
- `TenantAgentRuntimeInstallEntity`: TenantId Guid; keep RuntimeId (slug).
- `TenantSkillBundleBindingEntity`: TenantId Guid; keep BundleId (slug).
- `UnitPolicyEntity`: TenantId Guid; UnitId Guid (was string).
- `ActivityEventRecord`: TenantId Guid; `Source : string` -> `SourceId : Guid`.
- `PackageInstallEntity`: TenantId Guid.

## Slice C: Cascade Dapr layer

- Tenancy: StaticTenantContext, ConfiguredTenantContext, DefaultTenantBootstrapService, TenancyOptions, SecretsOptions
- All EntityConfigurations
- Repositories (Unit, UnitMembership, UnitSubunitMembership, Secret, ApiToken, etc.)
- Actors (UnitActor, AgentActor, HumanActor, ContainerSupervisorActor, etc.)
- Services / Tools (Tools/*, Capabilities/*, Skills/*, Policies/*)
- Routing / Directory
- Workflows
- Auth
- Endpoints (Host.Api/Endpoints/*)
- Models (Host.Api/Models/*)

## Slice D: Tests

- ActorTestBase consumers
- Test fixtures
- Dapr test mode
- TenantContext mocks/stubs

## Slice E: Schema reset

- Delete all migrations + Designer + ModelSnapshot under Data/Migrations
- Generate `InitialBaseline` migration
- Drop Dapr state store volume in dev compose

## Open questions / uncertainties

- `ConnectorDefinitionEntity.ConnectorId` slug: spec says drop slug from agent_definitions and unit_definitions; the issue body's "Slug placement" final-design comment says only those two have entity slugs to drop. Connectors are tenant-scoped definitions like agents and units. The "Slugs only stay on" list mentions `ConnectorDefinition.Slug` — but the final design overrides. I'll **drop** the ConnectorId slug column (no slugs anywhere).
- The Members JsonElement on UnitDefinitionEntity stores slug-form addresses. I'll drop that column or make it Guid IDs only — the Members list is no longer authoritative since memberships are edges.
- `DirectoryEntry.ActorId : Guid` — record param of `string` rename. I'll keep name `ActorId : Guid` rather than rename to `Id` to minimize churn.
