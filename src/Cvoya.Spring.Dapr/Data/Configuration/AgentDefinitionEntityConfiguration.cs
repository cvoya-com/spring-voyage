// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="AgentDefinitionEntity"/> type.
/// Applies snake_case naming, audit columns, and the tenant column +
/// index. The combined tenant + soft-delete query filter is applied on
/// the DbContext itself so the closure captures <c>this</c> and EF Core
/// re-evaluates the tenant id per instance.
/// </summary>
internal class AgentDefinitionEntityConfiguration : IEntityTypeConfiguration<AgentDefinitionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentDefinitionEntity> builder)
    {
        builder.ToTable("agent_definitions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ActorId).HasColumnName("actor_id").HasMaxLength(256);
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(e => e.Role).HasColumnName("role").HasMaxLength(512);
        builder.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(e => new { e.TenantId, e.AgentId }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasIndex(e => e.TenantId);
    }
}