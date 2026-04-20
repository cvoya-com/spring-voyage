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
    /// bound to <see cref="Cvoya.Spring.Dapr.Tenancy.ConfiguredTenantContext.DefaultTenantId"/>.
    /// Kept for the design-time factory and any test harness that has
    /// not yet been updated to pass an explicit tenant context. Runtime
    /// DI always takes the two-argument constructor.
    /// </summary>
    public SpringDbContext(DbContextOptions<SpringDbContext> options)
        : this(options, new StaticTenantContext(Cvoya.Spring.Dapr.Tenancy.ConfiguredTenantContext.DefaultTenantId))
    {
    }

    /// <summary>
    /// Current tenant id surfaced as a DbContext-level property so the
    /// per-entity query filters can reference <c>this.CurrentTenantId</c>.
    /// EF Core re-evaluates the filter closure against the specific
    /// context instance on every query, giving each instance its own
    /// tenant view — a requirement once the model cache is shared across
    /// instances (which it always is). Hidden from the public surface
    /// via <see cref="System.ComponentModel.EditorBrowsableAttribute"/>
    /// because it only exists to wire the query filter.
    /// </summary>
    internal string CurrentTenantId => _tenantContext.CurrentTenantId;

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

    /// <summary>Gets the set of unit-policy rows.</summary>
    public DbSet<UnitPolicyEntity> UnitPolicies => Set<UnitPolicyEntity>();

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
        modelBuilder.ApplyConfiguration(new UnitPolicyEntityConfiguration());

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
        modelBuilder.Entity<UnitPolicyEntity>()
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
                    && string.IsNullOrEmpty(tenantScoped.TenantId)
                    && entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(ITenantScopedEntity.TenantId)) is { } tenantIdProperty)
                {
                    tenantIdProperty.CurrentValue = _tenantContext.CurrentTenantId;
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
                    updatedAt.CurrentValue = now;
                }
            }
        }
    }
}