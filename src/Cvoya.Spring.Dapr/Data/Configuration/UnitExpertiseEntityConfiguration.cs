// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitExpertiseEntity"/>. One row
/// per <c>(tenant, unit, name)</c>; uniqueness is enforced via a unique
/// index. Tenant scoping is applied via the <c>HasQueryFilter</c> on
/// the <c>SpringDbContext</c>.
/// </summary>
internal class UnitExpertiseEntityConfiguration : IEntityTypeConfiguration<UnitExpertiseEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitExpertiseEntity> builder)
    {
        builder.ToTable("unit_expertise");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").IsRequired();
        builder.Property(e => e.Level)
            .HasColumnName("level")
            .HasConversion<int?>();
        builder.Property(e => e.InputSchemaJson)
            .HasColumnName("input_schema_json")
            .HasColumnType("text");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.UnitId, e.Name })
            .IsUnique()
            .HasDatabaseName("ux_unit_expertise_tenant_unit_name");

        builder.HasIndex(e => new { e.TenantId, e.UnitId })
            .HasDatabaseName("ix_unit_expertise_tenant_unit");
    }
}
