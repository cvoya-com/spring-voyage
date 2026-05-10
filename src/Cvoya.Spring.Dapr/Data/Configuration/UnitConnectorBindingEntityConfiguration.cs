// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitConnectorBindingEntity"/>.
/// At most one row per <c>(tenant_id, unit_id)</c>; rebinding to a
/// different connector type updates the existing row in place. Tenant
/// scoping is applied via <c>HasQueryFilter</c> on the
/// <c>SpringDbContext</c>.
/// </summary>
internal class UnitConnectorBindingEntityConfiguration : IEntityTypeConfiguration<UnitConnectorBindingEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitConnectorBindingEntity> builder)
    {
        builder.ToTable("unit_connector_bindings");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorType).HasColumnName("connector_type").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Config).HasColumnName("config").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        builder.Property(e => e.BoundAt).HasColumnName("bound_at").IsRequired();

        // Unique index enforces "at most one binding per unit per tenant".
        // Rebinds upsert into this row.
        builder.HasIndex(e => new { e.TenantId, e.UnitId })
            .IsUnique()
            .HasDatabaseName("ux_unit_connector_bindings_tenant_unit");

        // Secondary index supports cross-unit listing per connector type
        // (e.g. GET /api/v1/connectors/{slug}/bindings, #520).
        builder.HasIndex(e => new { e.TenantId, e.ConnectorType })
            .HasDatabaseName("ix_unit_connector_bindings_tenant_type");
    }
}
