// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantRecordEntity"/>. Applies
/// snake_case naming and the identity / audit columns. Note: this
/// entity is global (not <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopedEntity"/>),
/// so no tenant query filter is applied — only the soft-delete clause
/// configured on the DbContext.
/// </summary>
internal class TenantRecordEntityConfiguration : IEntityTypeConfiguration<TenantRecordEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantRecordEntity> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.State)
            .HasColumnName("state")
            .HasConversion<int>()
            .IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
    }
}