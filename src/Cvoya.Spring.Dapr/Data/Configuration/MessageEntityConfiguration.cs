// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="MessageEntity"/>. Single primary key
/// on <c>id</c>; foreign key to <c>threads.id</c> with cascade delete so a
/// thread teardown also reclaims its messages; index on
/// <c>(tenant_id, thread_id, sent_at)</c> serves the timeline-list query
/// shape the read-side rewrite (#2054) consumes. The tenant query filter is
/// applied on the DbContext.
/// </summary>
internal class MessageEntityConfiguration : IEntityTypeConfiguration<MessageEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MessageEntity> builder)
    {
        builder.ToTable("messages");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ThreadId).HasColumnName("thread_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.SenderScheme).HasColumnName("sender_scheme").IsRequired().HasMaxLength(32);
        builder.Property(e => e.SenderId).HasColumnName("sender_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.RecipientScheme).HasColumnName("recipient_scheme").IsRequired().HasMaxLength(32);
        builder.Property(e => e.RecipientId).HasColumnName("recipient_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.MessageType).HasColumnName("message_type").IsRequired().HasMaxLength(32);
        builder.Property(e => e.Body).HasColumnName("body").HasColumnType("text");
        builder.Property(e => e.Payload).HasColumnName("payload").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.SentAt).HasColumnName("sent_at").IsRequired();
        builder.Property(e => e.RetractedAt).HasColumnName("retracted_at");
        builder.Property(e => e.InReplyTo).HasColumnName("in_reply_to").HasColumnType("uuid");

        // Foreign key to threads.id. Cascade on delete so retiring a thread
        // row does not orphan its messages — the registry owns the thread
        // lifetime and downstream readers join through it.
        builder.HasOne<ThreadEntity>()
            .WithMany()
            .HasForeignKey(e => e.ThreadId)
            .HasConstraintName("fk_messages_thread_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Timeline query shape: every read for a thread is "give me the
        // messages for (tenant, thread) ordered by sent_at." The composite
        // index plus the unique PK on id covers both list and single-message
        // reads without a secondary scan.
        builder.HasIndex(e => new { e.TenantId, e.ThreadId, e.SentAt })
            .HasDatabaseName("ix_messages_tenant_thread_sent_at");
    }
}
