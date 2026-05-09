// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="TenantModelProviderInstallEntity"/>
/// type. Composite PK <c>(tenant_id, provider_id)</c>, snake_case column
/// names, JSONB config column. The combined tenant + soft-delete query
/// filter is applied on the DbContext itself so the closure captures
/// <c>this</c>.
/// </summary>
internal class TenantModelProviderInstallEntityConfiguration : IEntityTypeConfiguration<TenantModelProviderInstallEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantModelProviderInstallEntity> builder)
    {
        builder.ToTable("tenant_model_provider_installs");

        builder.HasKey(e => new { e.TenantId, e.ProviderId });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ProviderId).HasColumnName("provider_id").IsRequired().HasMaxLength(64);
        builder.Property(e => e.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.InstalledAt).HasColumnName("installed_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.TenantId);
    }
}
