// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="MemoryTopicEntity"/> (#2342).
/// Unique index on
/// <c>(tenant_id, owner_scheme, owner_id, name)</c> enforces the
/// "topic names are owner-unique" invariant.
/// </summary>
internal class MemoryTopicEntityConfiguration : IEntityTypeConfiguration<MemoryTopicEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryTopicEntity> builder)
    {
        builder.ToTable("memory_topics");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.OwnerScheme).HasColumnName("owner_scheme").IsRequired().HasMaxLength(16);
        builder.Property(e => e.OwnerId).HasColumnName("owner_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.OwnerScheme, e.OwnerId, e.Name })
            .IsUnique()
            .HasDatabaseName("ux_memory_topics_tenant_owner_name");
    }
}
