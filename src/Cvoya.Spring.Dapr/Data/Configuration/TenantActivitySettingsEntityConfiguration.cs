// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantActivitySettingsEntity"/>.
/// One row per tenant, keyed on the tenant id. No soft-delete column and
/// no <c>HasQueryFilter</c> — the OSS service implementation reads the
/// row through a tenant-aware query (filtered by the injected
/// <c>ITenantContext.CurrentTenantId</c>), and the cross-tenant
/// retention sweep uses <c>ITenantScopeBypass</c> like every other
/// global read.
/// </summary>
internal class TenantActivitySettingsEntityConfiguration
    : IEntityTypeConfiguration<TenantActivitySettingsEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantActivitySettingsEntity> builder)
    {
        builder.ToTable("tenant_activity_settings");

        builder.HasKey(e => e.TenantId);
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").HasColumnType("uuid");
        builder.Property(e => e.Level).HasColumnName("level").IsRequired().HasMaxLength(32);
        builder.Property(e => e.RetentionDays).HasColumnName("retention_days").IsRequired();
        // #2503: optional external OTel forwarding block, serialised as JSON.
        // jsonb in PostgreSQL; nullable so unconfigured tenants keep a
        // clean row.
        builder.Property(e => e.ExternalForwardConfig)
            .HasColumnName("external_forward_config")
            .HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }
}
