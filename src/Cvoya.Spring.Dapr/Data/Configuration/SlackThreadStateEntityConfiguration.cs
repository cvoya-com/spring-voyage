// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="SlackThreadStateEntity"/>. Maps
/// the entity to the <c>slack_thread_ts</c> table; declares the
/// outbound and inbound unique indexes.
/// </summary>
internal class SlackThreadStateEntityConfiguration : IEntityTypeConfiguration<SlackThreadStateEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlackThreadStateEntity> builder)
    {
        builder.ToTable("slack_thread_ts");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.SvThreadId).HasColumnName("sv_thread_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.BoundTenantUserId)
            .HasColumnName("bound_tenant_user_id")
            .IsRequired()
            .HasColumnType("uuid");
        builder.Property(e => e.TeamId)
            .HasColumnName("team_id")
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(e => e.SlackThreadTs)
            .HasColumnName("slack_thread_ts")
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(e => e.SlackChannelId)
            .HasColumnName("slack_channel_id")
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Outbound lookup: "do we already have a Slack thread for this
        // SV thread for this bound user?" One row per
        // (tenant, sv_thread, bound_user, team).
        builder.HasIndex(e => new
        {
            e.TenantId,
            e.SvThreadId,
            e.BoundTenantUserId,
            e.TeamId,
        })
            .IsUnique()
            .HasDatabaseName("ux_slack_thread_ts_outbound");

        // Inbound reverse lookup: "an event arrived on this thread_ts
        // — which SV thread is it?" A team_id can only host one
        // thread_ts per thread within a tenant.
        builder.HasIndex(e => new { e.TenantId, e.TeamId, e.SlackThreadTs })
            .IsUnique()
            .HasDatabaseName("ux_slack_thread_ts_inbound");
    }
}
