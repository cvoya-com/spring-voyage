// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data.Configuration;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core database context for the Spring Voyage platform.
/// Provides access to agent, unit, connector, activity event, and API token entities.
/// Uses the "spring" schema and applies snake_case naming, soft deletes, and audit columns.
///
/// <para>
/// Every business-data entity implements <see cref="ITenantScopedEntity"/>
/// and its <c>IEntityTypeConfiguration</c> applies a combined
/// <c>TenantId == tenantContext.CurrentTenantId &amp;&amp; DeletedAt == null</c>
/// query filter. The <see cref="ITenantContext"/> injected here is
/// threaded through to every configuration so the filter resolves the
/// current tenant at query time.
/// </para>
/// </summary>
public class SpringDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new <see cref="SpringDbContext"/> with an explicit
    /// tenant context. Runtime call sites resolve both via DI; test
    /// harnesses that construct the context manually pass a
    /// <see cref="StaticTenantContext"/> (or any <see cref="ITenantContext"/>
    /// implementation) to control the tenant used by the query filter.
    /// </summary>
    public SpringDbContext(DbContextOptions<SpringDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Back-compat constructor that falls back to the <see cref="StaticTenantContext"/>
    /// bound to <see cref="OssTenantIds.Default"/>. Kept for the
    /// design-time factory and any test harness that has not yet been
    /// updated to pass an explicit tenant context. Runtime DI always
    /// takes the two-argument constructor.
    /// </summary>
    public SpringDbContext(DbContextOptions<SpringDbContext> options)
        : this(options, new StaticTenantContext(OssTenantIds.Default))
    {
    }

    /// <summary>
    /// Current tenant id surfaced as a DbContext-level property so the
    /// per-entity query filters can reference <c>this.CurrentTenantId</c>.
    /// EF Core re-evaluates the filter closure against the specific
    /// context instance on every query, giving each instance its own
    /// tenant view — a requirement once the model cache is shared across
    /// instances (which it always is).
    /// </summary>
    internal Guid CurrentTenantId => _tenantContext.CurrentTenantId;

    /// <summary>Gets the set of agent definition entities.</summary>
    public DbSet<AgentDefinitionEntity> AgentDefinitions => Set<AgentDefinitionEntity>();

    /// <summary>Gets the set of unit definition entities.</summary>
    public DbSet<UnitDefinitionEntity> UnitDefinitions => Set<UnitDefinitionEntity>();

    /// <summary>Gets the set of connector definition entities.</summary>
    public DbSet<ConnectorDefinitionEntity> ConnectorDefinitions => Set<ConnectorDefinitionEntity>();

    /// <summary>Gets the set of activity event records.</summary>
    public DbSet<ActivityEventRecord> ActivityEvents => Set<ActivityEventRecord>();

    /// <summary>Gets the set of API token entities.</summary>
    public DbSet<ApiTokenEntity> ApiTokens => Set<ApiTokenEntity>();

    /// <summary>Gets the set of cost records.</summary>
    public DbSet<CostRecord> CostRecords => Set<CostRecord>();

    /// <summary>Gets the set of secret-registry entries.</summary>
    public DbSet<SecretRegistryEntry> SecretRegistryEntries => Set<SecretRegistryEntry>();

    /// <summary>Gets the set of unit-membership rows.</summary>
    public DbSet<UnitMembershipEntity> UnitMemberships => Set<UnitMembershipEntity>();

    /// <summary>Gets the persistent projection of parent → child unit edges (#1154).</summary>
    public DbSet<UnitSubunitMembershipEntity> UnitSubunitMemberships => Set<UnitSubunitMembershipEntity>();

    /// <summary>Gets the set of unit-policy rows.</summary>
    public DbSet<UnitPolicyEntity> UnitPolicies => Set<UnitPolicyEntity>();

    /// <summary>Gets the set of per-tenant model-provider install rows (#1770 / ADR-0038).</summary>
    public DbSet<TenantModelProviderInstallEntity> TenantModelProviderInstalls => Set<TenantModelProviderInstallEntity>();

    /// <summary>Gets the set of per-tenant connector install rows.</summary>
    public DbSet<TenantConnectorInstallEntity> TenantConnectorInstalls => Set<TenantConnectorInstallEntity>();

    /// <summary>Gets the set of credential-health rows (runtimes + connectors).</summary>
    public DbSet<CredentialHealthEntity> CredentialHealth => Set<CredentialHealthEntity>();

    /// <summary>
    /// Operational issues observed against units or agents (#2160). The
    /// open set (<c>ClearedAt is null</c>) is what the Overview tab
    /// surfaces; cleared rows are kept short-term for audit pending the
    /// retention story (#2174).
    /// </summary>
    public DbSet<IssueEntity> Issues => Set<IssueEntity>();

    /// <summary>Gets the set of per-tenant skill-bundle binding rows.</summary>
    public DbSet<TenantSkillBundleBindingEntity> TenantSkillBundleBindings => Set<TenantSkillBundleBindingEntity>();

    /// <summary>Gets the set of first-class tenant records (#1260 / C1.2d).</summary>
    public DbSet<TenantRecordEntity> Tenants => Set<TenantRecordEntity>();

    /// <summary>Gets the set of stable human identity records (#1491).</summary>
    public DbSet<HumanEntity> Humans => Set<HumanEntity>();

    /// <summary>
    /// Gets the set of <c>TenantUser</c> rows (ADR-0047 §1). Each row is
    /// an authenticated principal of Spring Voyage scoped to one tenant;
    /// the natural key is <c>(tenant_id, auth_subject)</c> with
    /// <c>auth_subject</c> nullable so OSS rows pinned by
    /// <see cref="Cvoya.Spring.Core.Tenancy.OssTenantUserIds.Operator"/>
    /// can exist without an OAuth subject claim.
    /// </summary>
    public DbSet<TenantUserEntity> TenantUsers => Set<TenantUserEntity>();

    /// <summary>
    /// Gets the set of <c>TenantUser</c> ↔ connector-native display-
    /// identity mapping rows (ADR-0047 §2). One row per
    /// <c>(tenant, tenant_user, connector)</c> tuple; backs
    /// <see cref="Cvoya.Spring.Core.Security.ITenantUserConnectorIdentityResolver"/>
    /// so the platform can resolve a connector login (e.g. a GitHub
    /// handle) to a stable <c>TenantUser</c> UUID and vice-versa. The
    /// row is strictly display-identity: no PAT, no installation
    /// override, no auth fields.
    /// </summary>
    public DbSet<TenantUserConnectorIdentityEntity> TenantUserConnectorIdentities => Set<TenantUserConnectorIdentityEntity>();

    /// <summary>
    /// Gets the set of tenant-scoped daily cost budget rows (ADR-0040 / #2045).
    /// Replaces the pre-ADR <c>Agent:CostBudget</c>, <c>Unit:CostBudget</c>,
    /// and <c>Tenant:CostBudget</c> actor-state keys with a single relational
    /// table keyed on <c>(tenant_id, scope_type, scope_id)</c>.
    /// </summary>
    public DbSet<BudgetLimitEntity> BudgetLimits => Set<BudgetLimitEntity>();

    /// <summary>
    /// Gets the set of package install tracking rows (#1558 / ADR-0035 decision 11).
    /// One row per (install_id, package_name); multi-package batches share the
    /// same <c>install_id</c>.
    /// </summary>
    public DbSet<PackageInstallEntity> PackageInstalls => Set<PackageInstallEntity>();

    /// <summary>
    /// Gets the set of thread-registry rows (#2047 / ADR-0030 / ADR-0040). One
    /// row per canonicalised participant set; the deterministic
    /// <see cref="ThreadEntity.ParticipantKey"/> is the public-API entry point
    /// for participant-set lookup.
    /// </summary>
    public DbSet<ThreadEntity> Threads => Set<ThreadEntity>();

    /// <summary>
    /// Gets the set of (unit, human) ACL grants (#2044 / ADR-0040). Replaces
    /// the actor-state <c>Unit:HumanPermissions</c> map with a tenant-scoped
    /// EF row so authorization reads become a single indexed SQL lookup.
    /// </summary>
    public DbSet<UnitHumanPermissionEntity> UnitHumanPermissions => Set<UnitHumanPermissionEntity>();

    /// <summary>
    /// Gets the set of package-declared team-membership rows for humans on
    /// units (ADR-0044). Sibling to <see cref="UnitHumanPermissions"/>:
    /// permissions capture platform ACLs; this table captures domain team
    /// roles declared in the package YAML's <c>humans:</c> block. One row
    /// per <c>(unit, human, role)</c> triple.
    /// </summary>
    public DbSet<UnitMembershipHumanEntity> UnitMembershipsHumans => Set<UnitMembershipHumanEntity>();

    /// <summary>
    /// Gets the set of persisted message-history rows (#2053 / ADR-0030 /
    /// ADR-0040). The dispatcher writes one row per accepted Domain
    /// message; readers query this table directly instead of scanning
    /// <c>activity_events.Details</c> JSON.
    /// </summary>
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    /// <summary>
    /// Gets the set of agent live-config rows (#2048 / ADR-0040). One row
    /// per agent; replaces the actor-state <c>Agent:Model</c>,
    /// <c>Agent:Specialty</c>, <c>Agent:Enabled</c>, and
    /// <c>Agent:ExecutionMode</c> keys.
    /// </summary>
    public DbSet<AgentLiveConfigEntity> AgentLiveConfigs => Set<AgentLiveConfigEntity>();

    /// <summary>
    /// Gets the set of agent tool-grant rows (#2335 Sub B). One row per
    /// <c>(tenant, agent, tool_name, provenance)</c>; replaces the
    /// pre-#2335 <c>agent_skill_grants</c> table with a shape that
    /// carries the provenance tier (platform / connector / image /
    /// explicit) on every row.
    /// </summary>
    public DbSet<AgentToolGrantEntity> AgentToolGrants => Set<AgentToolGrantEntity>();

    /// <summary>
    /// Gets the set of unit tool-grant rows (#2335 Sub B). Symmetric to
    /// <see cref="AgentToolGrants"/>; the auto-grant pipeline writes one
    /// row per <c>&lt;ToolNamespace&gt;.*</c> tool on connector bind and
    /// removes them on unbind.
    /// </summary>
    public DbSet<UnitToolGrantEntity> UnitToolGrants => Set<UnitToolGrantEntity>();

    /// <summary>
    /// Gets the set of agent expertise rows (#2048 / ADR-0040). One row
    /// per (agent, expertise-name); replaces the actor-state
    /// <c>Agent:Expertise</c> list.
    /// </summary>
    public DbSet<AgentExpertiseEntity> AgentExpertise => Set<AgentExpertiseEntity>();

    /// <summary>
    /// Gets the set of unit live-config rows (#2049 / ADR-0040). One row
    /// per unit; replaces the actor-state <c>Unit:Model</c>,
    /// <c>Unit:Color</c>, <c>Unit:Provider</c>, <c>Unit:Hosting</c>,
    /// <c>Unit:Boundary</c>, and <c>Unit:PermissionInheritance</c> keys.
    /// </summary>
    public DbSet<UnitLiveConfigEntity> UnitLiveConfigs => Set<UnitLiveConfigEntity>();

    /// <summary>
    /// Gets the set of unit own-expertise rows (#2049 / ADR-0040). One
    /// row per (unit, expertise-name); replaces the actor-state
    /// <c>Unit:OwnExpertise</c> list.
    /// </summary>
    public DbSet<UnitExpertiseEntity> UnitExpertise => Set<UnitExpertiseEntity>();

    /// <summary>
    /// Gets the set of unit connector binding rows (#2050 / ADR-0040).
    /// One row per (tenant, unit); replaces the actor-state
    /// <c>Unit:ConnectorBinding</c> + <c>Unit:ConnectorMetadata</c>
    /// pair with the connector type, typed config, and connector-owned
    /// runtime metadata (e.g. webhook ids) on a single relational row.
    /// </summary>
    public DbSet<UnitConnectorBindingEntity> UnitConnectorBindings => Set<UnitConnectorBindingEntity>();

    /// <summary>
    /// Gets the set of agent / tenant cloning-policy rows (#2051 /
    /// ADR-0040). One row per <c>(tenant_id, scope_type, scope_id)</c>;
    /// replaces the actor-state <c>Agent:CloningPolicy:{agentId}</c> and
    /// <c>Tenant:CloningPolicy:{tenantId}</c> keys with a tenant-scoped
    /// EF row whose <c>policy</c> column holds the serialised
    /// <see cref="Cvoya.Spring.Core.Cloning.AgentCloningPolicy"/> payload.
    /// </summary>
    public DbSet<CloningPolicyEntity> CloningPolicies => Set<CloningPolicyEntity>();

    /// <summary>
    /// Gets the set of memory entries (#2342). One row per memory
    /// (long-term or short-term); owner-scoped on
    /// <c>(tenant_id, owner_scheme, owner_id)</c> per ADR-0036, with a
    /// Postgres <c>GIN(to_tsvector('english', content))</c> index that
    /// backs the <c>sv.memory.search</c> tool.
    /// </summary>
    public DbSet<MemoryEntity> Memories => Set<MemoryEntity>();

    /// <summary>
    /// Gets the set of persistent-agent runtime rows (#2468). One row per
    /// agent currently tracked by <c>PersistentAgentRegistry</c>. Acts as
    /// the cross-process source of truth so an auto-deploy on the worker
    /// surfaces in API-process reads (the agent detail badge, runtime
    /// status chip, logs endpoint).
    /// </summary>
    public DbSet<PersistentAgentRuntimeEntity> PersistentAgentRuntime => Set<PersistentAgentRuntimeEntity>();

    /// <summary>
    /// Per-tenant activity-capture settings (#2492). One row per tenant
    /// keyed on <c>tenant_id</c>; absence resolves to
    /// <see cref="Cvoya.Spring.Core.Capabilities.ITenantActivitySettings.DefaultLevel"/>
    /// / <see cref="Cvoya.Spring.Core.Capabilities.ITenantActivitySettings.DefaultRetentionDays"/>
    /// at the service layer.
    /// </summary>
    public DbSet<TenantActivitySettingsEntity> TenantActivitySettings => Set<TenantActivitySettingsEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("spring");

        // Per-entity column + index + PK configuration stays in the
        // entity-specific configurations so each file holds the full
        // shape for its type. The tenant query filter itself is applied
        // here, on the DbContext, because it must reference
        // <c>this.CurrentTenantId</c> — EF Core re-evaluates the filter
        // closure against the context instance on every query, which is
        // the only portable way to get per-instance tenant scoping from
        // a shared model cache.
        modelBuilder.ApplyConfiguration(new AgentDefinitionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitDefinitionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ConnectorDefinitionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ActivityEventRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ApiTokenEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CostRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SecretRegistryEntryConfiguration());
        modelBuilder.ApplyConfiguration(new UnitMembershipEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitSubunitMembershipEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitPolicyEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantModelProviderInstallEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantConnectorInstallEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CredentialHealthEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantSkillBundleBindingEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantRecordEntityConfiguration());
        modelBuilder.ApplyConfiguration(new HumanEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantUserEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantUserConnectorIdentityEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PackageInstallEntityConfiguration());
        modelBuilder.ApplyConfiguration(new BudgetLimitEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ThreadEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitHumanPermissionEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitMembershipHumanEntityConfiguration());
        modelBuilder.ApplyConfiguration(new MessageEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AgentLiveConfigEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AgentToolGrantEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitToolGrantEntityConfiguration());
        modelBuilder.ApplyConfiguration(new AgentExpertiseEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitLiveConfigEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitExpertiseEntityConfiguration());
        modelBuilder.ApplyConfiguration(new UnitConnectorBindingEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CloningPolicyEntityConfiguration());
        modelBuilder.ApplyConfiguration(new MemoryEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PersistentAgentRuntimeEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TenantActivitySettingsEntityConfiguration());

        // Combined tenant + soft-delete query filters. Each filter
        // captures <c>this</c>, so EF Core parameterises the tenant-id
        // access at query time — one compiled model, many tenants.
        modelBuilder.Entity<AgentDefinitionEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
        modelBuilder.Entity<UnitDefinitionEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
        modelBuilder.Entity<ConnectorDefinitionEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
        modelBuilder.Entity<ActivityEventRecord>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ApiTokenEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<CostRecord>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SecretRegistryEntry>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<UnitMembershipEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<UnitSubunitMembershipEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<UnitPolicyEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantModelProviderInstallEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
        modelBuilder.Entity<TenantConnectorInstallEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId && e.DeletedAt == null);
        modelBuilder.Entity<CredentialHealthEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<IssueEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<IssueEntity>()
            .HasIndex(e => new { e.TenantId, e.SubjectKind, e.SubjectId, e.Source, e.Code })
            .IsUnique()
            .HasFilter("\"ClearedAt\" IS NULL");
        modelBuilder.Entity<IssueEntity>()
            .HasIndex(e => new { e.TenantId, e.SubjectKind, e.SubjectId, e.ClearedAt });
        modelBuilder.Entity<TenantSkillBundleBindingEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // First-class tenant records are global by design — no tenant
        // query filter, only soft-delete. The platform-tenant endpoints
        // gate access via the PlatformOperator role at the API layer
        // (#1260 / C1.2d). Callers that need to surface soft-deleted
        // tenants explicitly call .IgnoreQueryFilters().
        modelBuilder.Entity<TenantRecordEntity>()
            .HasQueryFilter(e => e.DeletedAt == null);

        // Human identity records: tenant-scoped, no soft-delete.
        modelBuilder.Entity<HumanEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // TenantUser principals (ADR-0047 §1): tenant-scoped, no soft-delete.
        // OSS deployments carry exactly one row (the operator) pinned by
        // OssTenantUserIds.Operator; cloud deployments carry many per tenant.
        modelBuilder.Entity<TenantUserEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // TenantUser ↔ connector display-identity mappings (ADR-0047 §2):
        // tenant-scoped, no soft-delete. Removed identities are hard-
        // deleted so both unique-index slots ((tenant, tenant_user,
        // connector) natural key + (tenant, connector, username)
        // reverse-lookup) free up immediately.
        modelBuilder.Entity<TenantUserConnectorIdentityEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Package install tracking: tenant-scoped, no soft-delete.
        modelBuilder.Entity<PackageInstallEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Budget limits: tenant-scoped, no soft-delete (ADR-0040 / #2045).
        modelBuilder.Entity<BudgetLimitEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Thread registry: tenant-scoped, no soft-delete (#2047 / ADR-0030).
        modelBuilder.Entity<ThreadEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit ACL grants: tenant-scoped, no soft-delete (#2044 / ADR-0040).
        modelBuilder.Entity<UnitHumanPermissionEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit team-membership rows: tenant-scoped, no soft-delete (ADR-0044).
        // Sibling to unit_human_permissions — see entity for the orthogonality
        // rationale. The unique-index invariant lives in
        // UnitMembershipHumanEntityConfiguration; this filter just gates
        // visibility per tenant per CONVENTIONS § 12.
        modelBuilder.Entity<UnitMembershipHumanEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Message history: tenant-scoped, no soft-delete (#2053 / ADR-0030).
        // Retraction is modelled as a populated <c>retracted_at</c> column;
        // the row stays for audit and surfaces with a "retracted" badge.
        modelBuilder.Entity<MessageEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Agent live config: tenant-scoped, no soft-delete (#2048 / ADR-0040).
        modelBuilder.Entity<AgentLiveConfigEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Agent tool grants: tenant-scoped, no soft-delete (#2335 Sub B).
        modelBuilder.Entity<AgentToolGrantEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit tool grants: tenant-scoped, no soft-delete (#2335 Sub B).
        modelBuilder.Entity<UnitToolGrantEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Agent expertise: tenant-scoped, no soft-delete (#2048 / ADR-0040).
        modelBuilder.Entity<AgentExpertiseEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit live config: tenant-scoped, no soft-delete (#2049 / ADR-0040).
        modelBuilder.Entity<UnitLiveConfigEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit own expertise: tenant-scoped, no soft-delete (#2049 / ADR-0040).
        modelBuilder.Entity<UnitExpertiseEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Unit connector bindings: tenant-scoped, no soft-delete (#2050 / ADR-0040).
        // Cleared bindings are deleted outright; rebinds upsert into the
        // existing row to preserve id stability.
        modelBuilder.Entity<UnitConnectorBindingEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Cloning policies: tenant-scoped, no soft-delete (#2051 / ADR-0040).
        // Empty policies are deleted outright; non-empty upserts replace
        // the policy payload in place to preserve row id stability. Tenant-
        // scope rows carry NULL scope_id (uniqueness enforced by partial
        // index in the entity configuration).
        modelBuilder.Entity<CloningPolicyEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Memory entries: tenant-scoped, no soft-delete (#2342). Owner
        // scope is enforced inside the store implementation on top of
        // the tenant filter.
        modelBuilder.Entity<MemoryEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Persistent agent runtime rows: tenant-scoped, no soft-delete
        // (#2468). Rows are inserted on Register, mutated on Mark*
        // / health-timer success, and hard-deleted on Undeploy /
        // StopContainer. There is no audit value in keeping a row
        // around once it no longer represents a tracked container.
        modelBuilder.Entity<PersistentAgentRuntimeEntity>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    /// <inheritdoc />
    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                // Auto-populate TenantId on insert when the caller did
                // not supply one. The query filter requires every row
                // to carry the current tenant id; doing this here keeps
                // tenancy centralised so individual write sites don't
                // have to plumb ITenantContext through every call path.
                if (entry.Entity is ITenantScopedEntity tenantScoped
                    && tenantScoped.TenantId == Guid.Empty
                    && entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(ITenantScopedEntity.TenantId)) is { } tenantIdProperty)
                {
                    tenantIdProperty.CurrentValue = _tenantContext.CurrentTenantId;
                }

                // #2335 Sub B: default ToolNamespace to Type on insert
                // when the caller did not supply one. Mirrors
                // IConnectorType.ToolNamespace's default-to-Slug
                // behaviour at the row layer so a connector_definitions
                // INSERT lands with a non-empty namespace even when the
                // write site predates Sub B.
                if (entry.Entity is ConnectorDefinitionEntity connectorDef
                    && string.IsNullOrEmpty(connectorDef.ToolNamespace))
                {
                    connectorDef.ToolNamespace = connectorDef.Type;
                }

                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == "CreatedAt") is { } createdAt)
                {
                    if ((DateTimeOffset)createdAt.CurrentValue! == default)
                    {
                        createdAt.CurrentValue = now;
                    }
                }

                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt") is { } updatedAtOnAdd)
                {
                    if ((DateTimeOffset)updatedAtOnAdd.CurrentValue! == default)
                    {
                        updatedAtOnAdd.CurrentValue = now;
                    }
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt") is { } updatedAt)
                {
                    // #2519 carve-out: PersistentAgentRuntimeEntity drives
                    // the cross-process freshness gate keyed on UpdatedAt.
                    // Probe-bookkeeping writes (failure counter increments,
                    // MarkUnhealthy flags) must not bump the column or the
                    // gate fires against the registry's own writes. The
                    // registry opts its "fresh container alive" signals
                    // (RegisterAsync, RecordDispatchHeartbeatAsync) into a
                    // bump by setting CurrentValue explicitly — that value
                    // falls through to the EF write untouched here. All
                    // other Modified writes to this entity leave UpdatedAt
                    // alone, which is exactly the freshness-gate semantic
                    // we need.
                    if (entry.Entity is not PersistentAgentRuntimeEntity)
                    {
                        updatedAt.CurrentValue = now;
                    }
                }
            }
        }
    }
}
