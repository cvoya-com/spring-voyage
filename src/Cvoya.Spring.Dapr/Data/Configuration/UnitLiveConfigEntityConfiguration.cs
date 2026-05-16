// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitLiveConfigEntity"/>. The row
/// is keyed by <c>unit_id</c> (1:1 with the unit), tenant-scoped via the
/// <c>HasQueryFilter</c> applied on the <c>SpringDbContext</c>, and
/// indexed on <c>tenant_id</c> for cross-unit listings (e.g. permission
/// inheritance walks).
/// </summary>
internal class UnitLiveConfigEntityConfiguration : IEntityTypeConfiguration<UnitLiveConfigEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitLiveConfigEntity> builder)
    {
        builder.ToTable("unit_live_config");

        builder.HasKey(e => e.UnitId);

        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Model).HasColumnName("model").HasMaxLength(256);
        builder.Property(e => e.Color).HasColumnName("color").HasMaxLength(64);
        builder.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(128);
        builder.Property(e => e.Hosting).HasColumnName("hosting").HasMaxLength(64);
        // #2341: agent-parity columns. Same shape as AgentLiveConfigEntityConfiguration.
        builder.Property(e => e.Specialty).HasColumnName("specialty").HasMaxLength(256);
        builder.Property(e => e.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
        builder.Property(e => e.ExecutionMode)
            .HasColumnName("execution_mode")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.PermissionInheritance)
            .HasColumnName("permission_inheritance")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.Boundary).HasColumnName("boundary").HasColumnType("jsonb");
        builder.Property(e => e.ExpertiseInitialised)
            .HasColumnName("expertise_initialised")
            .IsRequired()
            .HasDefaultValue(false);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => e.TenantId);
    }
}
