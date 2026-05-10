// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="AgentExpertiseEntity"/>. One row
/// per <c>(tenant, agent, name)</c>; uniqueness is enforced via a unique
/// index. Tenant scoping is applied via the <c>HasQueryFilter</c> on the
/// <c>SpringDbContext</c>.
/// </summary>
internal class AgentExpertiseEntityConfiguration : IEntityTypeConfiguration<AgentExpertiseEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentExpertiseEntity> builder)
    {
        builder.ToTable("agent_expertise");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").IsRequired();
        builder.Property(e => e.Level)
            .HasColumnName("level")
            .HasConversion<int?>();
        builder.Property(e => e.InputSchemaJson)
            .HasColumnName("input_schema_json")
            .HasColumnType("text");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.AgentId, e.Name })
            .IsUnique()
            .HasDatabaseName("ux_agent_expertise_tenant_agent_name");

        builder.HasIndex(e => new { e.TenantId, e.AgentId })
            .HasDatabaseName("ix_agent_expertise_tenant_agent");
    }
}
