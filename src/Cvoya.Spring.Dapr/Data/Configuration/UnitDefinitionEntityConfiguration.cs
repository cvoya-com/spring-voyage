/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="UnitDefinitionEntity"/> type.
/// Applies snake_case naming, audit columns, soft-delete query filter, and tenant relationship.
/// </summary>
internal class UnitDefinitionEntityConfiguration : IEntityTypeConfiguration<UnitDefinitionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitDefinitionEntity> builder)
    {
        builder.ToTable("unit_definitions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
        builder.Property(e => e.Members).HasColumnName("members").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId);
        builder.HasIndex(e => new { e.TenantId, e.UnitId }).IsUnique().HasFilter("deleted_at IS NULL");

        builder.HasQueryFilter(e => e.DeletedAt == null);
    }
}
