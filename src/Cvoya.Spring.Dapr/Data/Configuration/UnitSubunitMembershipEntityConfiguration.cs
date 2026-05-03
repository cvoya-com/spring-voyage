// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitSubunitMembershipEntity"/>.
/// Composite primary key on (tenant_id, parent_id, child_id) with every
/// column typed as Guid; secondary index covers list-by-child
/// (list-by-parent is already covered by the PK prefix). The
/// <c>parent_id</c> column is polymorphic — it may target the tenants
/// table (for top-level units) or the unit_definitions table — so no
/// FK constraint is configured at the database level. The tenant query
/// filter is applied on the DbContext.
/// </summary>
internal class UnitSubunitMembershipEntityConfiguration : IEntityTypeConfiguration<UnitSubunitMembershipEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitSubunitMembershipEntity> builder)
    {
        builder.ToTable("unit_subunit_memberships");

        builder.HasKey(e => new { e.TenantId, e.ParentId, e.ChildId });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ParentId).HasColumnName("parent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ChildId).HasColumnName("child_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.ChildId }).HasDatabaseName("ix_unit_subunit_memberships_tenant_child");
    }
}
