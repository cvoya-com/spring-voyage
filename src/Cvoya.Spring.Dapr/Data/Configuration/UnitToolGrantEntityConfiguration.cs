// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitToolGrantEntity"/> (#2335 Sub B).
/// Symmetric to <see cref="AgentToolGrantEntityConfiguration"/>; see that
/// file for the design narrative.
/// </summary>
internal class UnitToolGrantEntityConfiguration : IEntityTypeConfiguration<UnitToolGrantEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitToolGrantEntity> builder)
    {
        builder.ToTable("unit_tool_grants");

        builder.HasKey(e => new { e.TenantId, e.UnitId, e.ToolName, e.Provenance });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Namespace).HasColumnName("namespace").IsRequired().HasMaxLength(64);
        builder.Property(e => e.ToolName).HasColumnName("tool_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Provenance).HasColumnName("provenance").IsRequired().HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.UnitId })
            .HasDatabaseName("ix_unit_tool_grants_tenant_unit");

        builder.HasIndex(e => new { e.TenantId, e.UnitId, e.Provenance })
            .HasDatabaseName("ix_unit_tool_grants_tenant_unit_provenance");

        builder.HasIndex(e => new { e.TenantId, e.Namespace })
            .HasDatabaseName("ix_unit_tool_grants_tenant_namespace");
    }
}
