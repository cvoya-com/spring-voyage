// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitSubunitMembershipEntity"/>.
/// Composite primary key on (tenant_id, parent_unit_id, child_unit_id);
/// secondary index covers list-by-child (list-by-parent is already
/// covered by the PK prefix). The tenant query filter is applied on the
/// DbContext.
/// </summary>
internal class UnitSubunitMembershipEntityConfiguration : IEntityTypeConfiguration<UnitSubunitMembershipEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitSubunitMembershipEntity> builder)
    {
        builder.ToTable("unit_subunit_memberships");

        builder.HasKey(e => new { e.TenantId, e.ParentUnitId, e.ChildUnitId });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ParentUnitId).HasColumnName("parent_unit_id").IsRequired().HasMaxLength(256);
        builder.Property(e => e.ChildUnitId).HasColumnName("child_unit_id").IsRequired().HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.ChildUnitId }).HasDatabaseName("ix_unit_subunit_memberships_tenant_child");
        // (tenant_id, parent_unit_id) is the PK prefix, so list-by-parent
        // already has a covering index.
    }
}