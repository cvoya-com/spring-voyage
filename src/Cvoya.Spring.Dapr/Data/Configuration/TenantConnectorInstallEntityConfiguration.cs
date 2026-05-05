// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="TenantConnectorInstallEntity"/>
/// type. Synthetic <c>id</c> PK + partial unique indexes that enforce one
/// row per scope (#1671):
/// <list type="bullet">
///   <item><description>
///     Tenant-level rows: <c>(tenant_id, connector_id)</c> unique among
///     rows where <c>package_install_id IS NULL AND unit_id IS NULL</c>.
///   </description></item>
///   <item><description>
///     Package-scope: <c>(tenant_id, connector_id, package_install_id)</c>
///     unique where <c>package_install_id IS NOT NULL AND unit_id IS NULL</c>.
///   </description></item>
///   <item><description>
///     Unit-scope: <c>(tenant_id, connector_id, unit_id)</c> unique where
///     <c>unit_id IS NOT NULL</c>.
///   </description></item>
/// </list>
/// </summary>
internal class TenantConnectorInstallEntityConfiguration : IEntityTypeConfiguration<TenantConnectorInstallEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantConnectorInstallEntity> builder)
    {
        builder.ToTable("tenant_connector_installs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorId).HasColumnName("connector_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.InstalledAt).HasColumnName("installed_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        builder.Property(e => e.PackageInstallId).HasColumnName("package_install_id").HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").HasColumnType("uuid");

        builder.HasIndex(e => e.TenantId);

        // Tenant-level uniqueness: one row per (tenant, slug) where both
        // discriminators are null. Partial unique index keeps the legacy
        // semantic intact while letting package-/unit-scope rows for the
        // same slug coexist.
        builder.HasIndex(e => new { e.TenantId, e.ConnectorId })
            .IsUnique()
            .HasFilter("\"package_install_id\" IS NULL AND \"unit_id\" IS NULL")
            .HasDatabaseName("ix_tenant_connector_installs_tenant_slug");

        builder.HasIndex(e => new { e.TenantId, e.ConnectorId, e.PackageInstallId })
            .IsUnique()
            .HasFilter("\"package_install_id\" IS NOT NULL AND \"unit_id\" IS NULL")
            .HasDatabaseName("ix_tenant_connector_installs_pkg_scope");

        builder.HasIndex(e => new { e.TenantId, e.ConnectorId, e.UnitId })
            .IsUnique()
            .HasFilter("\"unit_id\" IS NOT NULL")
            .HasDatabaseName("ix_tenant_connector_installs_unit_scope");
    }
}