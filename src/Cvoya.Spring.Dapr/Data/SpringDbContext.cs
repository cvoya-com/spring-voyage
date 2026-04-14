// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core database context for the Spring Voyage platform.
/// Provides access to agent, unit, connector, activity event, and API token entities.
/// Uses the "spring" schema and applies snake_case naming, soft deletes, and audit columns.
/// </summary>
public class SpringDbContext(DbContextOptions<SpringDbContext> options) : DbContext(options)
{
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