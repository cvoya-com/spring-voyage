// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantConnectorBindingEntity"/>.
/// At most one row per <c>(tenant_id, connector_slug)</c>; rebinding to
/// the same slug updates the existing row in place. Tenant scoping is
/// applied via <c>HasQueryFilter</c> on the
/// <see cref="SpringDbContext"/>.
/// </summary>
internal class TenantConnectorBindingEntityConfiguration : IEntityTypeConfiguration<TenantConnectorBindingEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantConnectorBindingEntity> builder)
    {
        builder.ToTable("tenant_connector_bindings");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorSlug)
            .HasColumnName("connector_slug")
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(e => e.ConnectorType).HasColumnName("connector_type").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Config).HasColumnName("config").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        builder.Property(e => e.BoundAt).HasColumnName("bound_at").IsRequired();

        // Unique index enforces "at most one binding per tenant per
        // connector slug". Rebinds upsert into this row.
        builder.HasIndex(e => new { e.TenantId, e.ConnectorSlug })
            .IsUnique()
            .HasDatabaseName("ux_tenant_connector_bindings_tenant_slug");
    }
}
