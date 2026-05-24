// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="ThreadEntity"/>. Single primary key on
/// <c>id</c>; unique index on <c>(tenant_id, participant_key)</c> enforces the
/// participant-set identity rule from ADR-0030 — concurrent inserts for the
/// same canonicalised participant set converge on a single row. The tenant
/// query filter is applied on the DbContext.
/// </summary>
internal class ThreadEntityConfiguration : IEntityTypeConfiguration<ThreadEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ThreadEntity> builder)
    {
        builder.ToTable("threads");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ParticipantKey).HasColumnName("participant_key").IsRequired().HasMaxLength(2048);
        builder.Property(e => e.Participants).HasColumnName("participants").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.ParticipantNameSnapshots)
            .HasColumnName("participant_name_snapshots")
            .IsRequired()
            .HasColumnType("jsonb")
            .HasDefaultValue("{}");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.LastActivityAt).HasColumnName("last_activity_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.ParticipantKey })
            .IsUnique()
            .HasDatabaseName("ux_threads_tenant_participant_key");
    }
}
