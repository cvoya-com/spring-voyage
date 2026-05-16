// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="AgentToolGrantEntity"/> (#2335 Sub B).
/// Replaces the pre-#2335 <c>agent_skill_grants</c> configuration.
/// </summary>
internal class AgentToolGrantEntityConfiguration : IEntityTypeConfiguration<AgentToolGrantEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentToolGrantEntity> builder)
    {
        builder.ToTable("agent_tool_grants");

        // Composite PK matching the brief's declared shape — one row per
        // (tenant, agent, tool, provenance). No synthetic id column: every
        // read path is keyed on the tuple anyway, and a synthetic id would
        // only invite duplicate-row bugs the resolver would have to dedupe.
        builder.HasKey(e => new { e.TenantId, e.AgentId, e.ToolName, e.Provenance });

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Namespace).HasColumnName("namespace").IsRequired().HasMaxLength(64);
        builder.Property(e => e.ToolName).HasColumnName("tool_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Provenance).HasColumnName("provenance").IsRequired().HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Common access pattern: list every grant for one agent.
        builder.HasIndex(e => new { e.TenantId, e.AgentId })
            .HasDatabaseName("ix_agent_tool_grants_tenant_agent");

        // "What rows did the github binding write?" pattern — narrow the
        // delete on unbind without a table scan.
        builder.HasIndex(e => new { e.TenantId, e.AgentId, e.Provenance })
            .HasDatabaseName("ix_agent_tool_grants_tenant_agent_provenance");

        // Namespace-scoped grant lookups.
        builder.HasIndex(e => new { e.TenantId, e.Namespace })
            .HasDatabaseName("ix_agent_tool_grants_tenant_namespace");
    }
}
