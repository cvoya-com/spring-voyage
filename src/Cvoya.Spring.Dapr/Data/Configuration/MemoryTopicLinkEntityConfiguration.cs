// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="MemoryTopicLinkEntity"/>
/// (#2342). Composite PK on
/// <c>(tenant_id, memory_id, topic_id)</c> enforces "at most one link
/// row per pair". Secondary indexes on each side back the cascade-
/// delete reads (topic delete looks up by <c>topic_id</c>; memory
/// delete looks up by <c>memory_id</c>).
/// </summary>
internal class MemoryTopicLinkEntityConfiguration : IEntityTypeConfiguration<MemoryTopicLinkEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MemoryTopicLinkEntity> builder)
    {
        builder.ToTable("memory_topic_links");

        builder.HasKey(e => new { e.TenantId, e.MemoryId, e.TopicId });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.MemoryId).HasColumnName("memory_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TopicId).HasColumnName("topic_id").IsRequired().HasColumnType("uuid");

        builder.HasIndex(e => new { e.TenantId, e.MemoryId })
            .HasDatabaseName("ix_memory_topic_links_tenant_memory");

        builder.HasIndex(e => new { e.TenantId, e.TopicId })
            .HasDatabaseName("ix_memory_topic_links_tenant_topic");
    }
}
