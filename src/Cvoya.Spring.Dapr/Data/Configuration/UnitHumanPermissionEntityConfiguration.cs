// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitHumanPermissionEntity"/>.
/// One row per <c>(tenant, unit, human)</c> direct permission grant; the
/// uniqueness invariant is enforced by a unique index on
/// <c>(tenant_id, unit_id, human_id)</c>. The tenant query filter itself
/// is applied on the DbContext so it can reference the per-instance
/// <c>CurrentTenantId</c> closure.
/// </summary>
internal class UnitHumanPermissionEntityConfiguration : IEntityTypeConfiguration<UnitHumanPermissionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitHumanPermissionEntity> builder)
    {
        builder.ToTable("unit_human_permissions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.HumanId).HasColumnName("human_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.PermissionLevel)
            .HasColumnName("permission_level")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.Identity).HasColumnName("identity").HasMaxLength(256);
        builder.Property(e => e.Notifications).HasColumnName("notifications").IsRequired().HasDefaultValue(true);
        builder.Property(e => e.GrantedAt).HasColumnName("granted_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.UnitId, e.HumanId })
            .IsUnique()
            .HasDatabaseName("ux_unit_human_permissions_tenant_unit_human");
    }
}
