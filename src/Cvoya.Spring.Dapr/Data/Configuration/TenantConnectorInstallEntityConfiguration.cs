// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="TenantConnectorInstallEntity"/>
/// type. Composite PK <c>(tenant_id, connector_id)</c> where
/// <c>connector_id</c> is the connector slug.
/// </summary>
internal class TenantConnectorInstallEntityConfiguration : IEntityTypeConfiguration<TenantConnectorInstallEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantConnectorInstallEntity> builder)
    {
        builder.ToTable("tenant_connector_installs");

        builder.HasKey(e => new { e.TenantId, e.ConnectorId });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorId).HasColumnName("connector_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.InstalledAt).HasColumnName("installed_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.TenantId);
    }
}