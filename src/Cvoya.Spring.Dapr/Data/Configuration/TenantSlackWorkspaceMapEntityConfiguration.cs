// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantSlackWorkspaceMapEntity"/>.
/// One row per Slack workspace (uniqueness on <c>team_id</c>); cross-
/// tenant query filter intentionally absent so the Slack inbound
/// webhook handler can resolve <c>team_id → tenant_id</c> regardless
/// of the current tenant context (ADR-0061 §7.5).
/// </summary>
internal class TenantSlackWorkspaceMapEntityConfiguration : IEntityTypeConfiguration<TenantSlackWorkspaceMapEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantSlackWorkspaceMapEntity> builder)
    {
        builder.ToTable("tenant_slack_workspace_map");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TeamId)
            .HasColumnName("team_id")
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TeamName)
            .HasColumnName("team_name")
            .HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Unique on team_id — one Slack workspace cannot map to two
        // SV tenants in any deployment.
        builder.HasIndex(e => e.TeamId)
            .IsUnique()
            .HasDatabaseName("ux_tenant_slack_workspace_map_team_id");

        // Secondary index supports "list workspaces bound to this
        // tenant" so the cloud disconnect flow can iterate without a
        // full scan.
        builder.HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_tenant_slack_workspace_map_tenant_id");
    }
}
