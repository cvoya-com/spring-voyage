// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="AgentLiveConfigEntity"/>. The row
/// is keyed by <c>agent_id</c> (1:1 with the agent), tenant-scoped via the
/// <c>HasQueryFilter</c> applied on the <c>SpringDbContext</c>, and
/// indexed on <c>tenant_id</c> for cross-agent listings (e.g. unit member
/// projections).
/// </summary>
internal class AgentLiveConfigEntityConfiguration : IEntityTypeConfiguration<AgentLiveConfigEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentLiveConfigEntity> builder)
    {
        builder.ToTable("agent_live_config");

        builder.HasKey(e => e.AgentId);

        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        // ADR-0067 §2 (#3111): the agent's model single home is the jsonb
        // execution block — agent_live_config.model is dropped.
        builder.Property(e => e.Specialty).HasColumnName("specialty").HasMaxLength(256);
        builder.Property(e => e.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(e => e.ExecutionMode)
            .HasColumnName("execution_mode")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.ExpertiseInitialised)
            .HasColumnName("expertise_initialised")
            .IsRequired()
            .HasDefaultValue(false);
        builder.Property(e => e.LifecycleStatus)
            .HasColumnName("lifecycle_status")
            .IsRequired()
            .HasConversion<int>()
            .HasDefaultValue(Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Draft);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => e.TenantId);
    }
}
