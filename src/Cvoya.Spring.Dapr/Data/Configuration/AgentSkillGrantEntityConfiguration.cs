// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="AgentSkillGrantEntity"/>. One row
/// per <c>(tenant, agent, skill_name)</c>; uniqueness is enforced via a
/// unique index covering all three columns. Tenant scoping is applied
/// via the <c>HasQueryFilter</c> on the <c>SpringDbContext</c>.
/// </summary>
internal class AgentSkillGrantEntityConfiguration : IEntityTypeConfiguration<AgentSkillGrantEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentSkillGrantEntity> builder)
    {
        builder.ToTable("agent_skill_grants");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.SkillName).HasColumnName("skill_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.GrantedAt).HasColumnName("granted_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.AgentId, e.SkillName })
            .IsUnique()
            .HasDatabaseName("ux_agent_skill_grants_tenant_agent_skill");

        // Common access pattern: list all grants for one agent.
        builder.HasIndex(e => new { e.TenantId, e.AgentId })
            .HasDatabaseName("ix_agent_skill_grants_tenant_agent");
    }
}
