// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="CloningPolicyEntity"/> (#2051 /
/// ADR-0040). One row per <c>(tenant_id, scope_type, scope_id)</c>;
/// agent-scope rows carry the agent Guid in <c>scope_id</c>, the
/// tenant-scope row carries <c>NULL</c>. Uniqueness is split across two
/// partial indexes — a regular unique index over rows with non-null
/// <c>scope_id</c> (the agent case) and a partial unique index over rows
/// where <c>scope_id IS NULL</c> (the tenant-scope case). The combined
/// approach matches <c>budget_limits</c> and side-steps Postgres's
/// "nulls are distinct" semantics in unique indexes without needing a
/// sentinel Guid for the tenant row. The tenant query filter is applied
/// on the <see cref="SpringDbContext"/>.
/// </summary>
internal class CloningPolicyEntityConfiguration : IEntityTypeConfiguration<CloningPolicyEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CloningPolicyEntity> builder)
    {
        builder.ToTable("cloning_policies");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ScopeType).HasColumnName("scope_type").IsRequired().HasMaxLength(16);
        builder.Property(e => e.ScopeId).HasColumnName("scope_id").HasColumnType("uuid");
        builder.Property(e => e.Policy).HasColumnName("policy").IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Agent-scope rows: scope_id is non-null; uniqueness across
        // (tenant_id, scope_type, scope_id) so each agent has at most one
        // policy row per tenant.
        builder.HasIndex(e => new { e.TenantId, e.ScopeType, e.ScopeId })
            .HasDatabaseName("ix_cloning_policies_tenant_scope")
            .IsUnique()
            .HasFilter("scope_id IS NOT NULL");

        // Tenant-scope rows: scope_id is null; one row per
        // (tenant_id, scope_type) where scope_type = 'tenant'.
        builder.HasIndex(e => new { e.TenantId, e.ScopeType })
            .HasDatabaseName("ix_cloning_policies_tenant_scope_null")
            .IsUnique()
            .HasFilter("scope_id IS NULL");
    }
}
