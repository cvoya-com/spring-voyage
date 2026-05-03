// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitMembershipEntity"/>. Composite
/// primary key on (tenant_id, unit_id, agent_id) with every column typed
/// as Guid. The tenant query filter is applied on the DbContext.
/// </summary>
internal class UnitMembershipEntityConfiguration : IEntityTypeConfiguration<UnitMembershipEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitMembershipEntity> builder)
    {
        builder.ToTable("unit_memberships");

        builder.HasKey(e => new { e.TenantId, e.UnitId, e.AgentId });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Model).HasColumnName("model").HasMaxLength(256);
        builder.Property(e => e.Specialty).HasColumnName("specialty").HasMaxLength(256);
        builder.Property(e => e.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(e => e.ExecutionMode).HasColumnName("execution_mode").HasConversion<int?>();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.IsPrimary).HasColumnName("is_primary").IsRequired().HasDefaultValue(false);

        builder.HasIndex(e => new { e.TenantId, e.AgentId }).HasDatabaseName("ix_unit_memberships_tenant_agent_id");
    }
}
