// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="MemoryEntity"/> (#2342). The
/// composite secondary index on
/// <c>(tenant_id, owner_scheme, owner_id, kind, created_at)</c> backs
/// the owner-scoped list path; a GIN index on
/// <c>to_tsvector('english', content)</c> backs the full-text search
/// path (created in the migration — EF Core cannot model a functional
/// index directly). <c>content</c> is a <c>jsonb</c> column, so the
/// index uses the <c>to_tsvector(jsonb)</c> overload (string-value
/// extraction).
/// </summary>
internal class MemoryEntityConfiguration : IEntityTypeConfiguration<MemoryEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryEntity> builder)
    {
        builder.ToTable("memories");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.OwnerScheme).HasColumnName("owner_scheme").IsRequired().HasMaxLength(16);
        builder.Property(e => e.OwnerId).HasColumnName("owner_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        builder.Property(e => e.ThreadId).HasColumnName("thread_id").HasColumnType("uuid");
        builder.Property(e => e.Content).HasColumnName("content").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Owner-scoped list path: filter by (tenant, owner) and order
        // by created_at desc. Composite index keeps the list cheap.
        builder.HasIndex(e => new { e.TenantId, e.OwnerScheme, e.OwnerId, e.Kind, e.CreatedAt })
            .HasDatabaseName("ix_memories_tenant_owner_kind_created");

        // Short-term entries are scoped on (owner, thread). A secondary
        // index keeps "list this thread's working memory" fast.
        builder.HasIndex(e => new { e.TenantId, e.OwnerScheme, e.OwnerId, e.ThreadId })
            .HasDatabaseName("ix_memories_tenant_owner_thread");
    }
}
