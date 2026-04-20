// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="TenantAgentRuntimeInstallEntity"/>
/// type. Composite PK <c>(tenant_id, runtime_id)</c>, snake_case column
/// names, JSONB config column. The combined tenant + soft-delete query
/// filter is applied on the DbContext itself so the closure captures
/// <c>this</c>.
/// </summary>
internal class TenantAgentRuntimeInstallEntityConfiguration : IEntityTypeConfiguration<TenantAgentRuntimeInstallEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantAgentRuntimeInstallEntity> builder)
    {
        builder.ToTable("tenant_agent_runtime_installs");

        builder.HasKey(e => new { e.TenantId, e.RuntimeId });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.RuntimeId).HasColumnName("runtime_id").IsRequired().HasMaxLength(64);
        builder.Property(e => e.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.InstalledAt).HasColumnName("installed_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => e.TenantId);
    }
}