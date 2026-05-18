// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="PersistentAgentRuntimeEntity"/>
/// (#2468). The row is keyed by <c>agent_id</c> (1:1 with the agent),
/// tenant-scoped via the <c>HasQueryFilter</c> applied on the
/// <c>SpringDbContext</c>, and indexed on <c>tenant_id</c> for
/// cross-agent listings (e.g. the future "show me everything that's
/// running in this tenant" surface).
/// </summary>
internal class PersistentAgentRuntimeEntityConfiguration : IEntityTypeConfiguration<PersistentAgentRuntimeEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PersistentAgentRuntimeEntity> builder)
    {
        builder.ToTable("persistent_agent_runtime");

        builder.HasKey(e => e.AgentId);

        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Endpoint).HasColumnName("endpoint").IsRequired().HasMaxLength(2048);
        builder.Property(e => e.ContainerId).HasColumnName("container_id").HasMaxLength(256);
        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.HealthStatus)
            .HasColumnName("health_status")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.ConsecutiveFailures).HasColumnName("consecutive_failures").IsRequired();
        builder.Property(e => e.SidecarId).HasColumnName("sidecar_id").HasMaxLength(256);
        builder.Property(e => e.SidecarNetworkName).HasColumnName("sidecar_network_name").HasMaxLength(256);
        builder.Property(e => e.Image).HasColumnName("image").HasMaxLength(1024);
        builder.Property(e => e.OwnerHost).HasColumnName("owner_host").HasMaxLength(512);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => e.TenantId);
    }
}
