// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="ConnectorDefinitionEntity"/> type.
/// Applies snake_case naming, audit columns, soft-delete query filter, and tenant relationship.
/// </summary>
internal class ConnectorDefinitionEntityConfiguration : IEntityTypeConfiguration<ConnectorDefinitionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConnectorDefinitionEntity> builder)
    {
        builder.ToTable("connector_definitions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.ConnectorId).HasColumnName("connector_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Type).HasColumnName("type").IsRequired().HasMaxLength(64);
        builder.Property(e => e.Config).HasColumnName("config").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId);
        builder.HasIndex(e => new { e.TenantId, e.ConnectorId }).IsUnique().HasFilter("deleted_at IS NULL");

        builder.HasQueryFilter(e => e.DeletedAt == null);
    }
}
