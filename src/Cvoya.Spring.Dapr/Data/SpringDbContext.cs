/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Dapr.Data.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core database context for the Spring Voyage platform.
/// Provides access to tenant, agent, unit, connector, activity event, and API token entities.
/// Uses the "spring" schema and applies snake_case naming, soft deletes, and audit columns.
/// </summary>
public class SpringDbContext(DbContextOptions<SpringDbContext> options) : DbContext(options)
{
    /// <summary>Gets the set of tenant entities.</summary>
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();

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

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("spring");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SpringDbContext).Assembly);
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
                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(TenantEntity.CreatedAt)) is { } createdAt)
                {
                    if ((DateTimeOffset)createdAt.CurrentValue! == default)
                    {
                        createdAt.CurrentValue = now;
                    }
                }

                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(TenantEntity.UpdatedAt)) is { } updatedAtOnAdd)
                {
                    if ((DateTimeOffset)updatedAtOnAdd.CurrentValue! == default)
                    {
                        updatedAtOnAdd.CurrentValue = now;
                    }
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                if (entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(TenantEntity.UpdatedAt)) is { } updatedAt)
                {
                    updatedAt.CurrentValue = now;
                }
            }
        }
    }
}
