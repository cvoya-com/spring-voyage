// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="BudgetLimitEntity"/>. Persists tenant-
/// scoped daily cost budgets in the <c>budget_limits</c> table; replaces the
/// pre-ADR-0040 actor-state keys <c>Agent:CostBudget</c>, <c>Unit:CostBudget</c>,
/// and <c>Tenant:CostBudget</c>. Uniqueness across <c>(tenant_id, scope_type,
/// scope_id)</c> is enforced by two partial indexes — a regular unique index
/// over rows with a non-null <c>scope_id</c> (the agent / unit cases) and a
/// partial unique index over rows where <c>scope_id IS NULL</c> (the
/// tenant-scope case). The combined approach side-steps Postgres's "nulls are
/// distinct" semantics in unique indexes without needing a sentinel Guid for
/// the tenant row. The tenant query filter is applied on the DbContext.
/// </summary>
internal class BudgetLimitEntityConfiguration : IEntityTypeConfiguration<BudgetLimitEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<BudgetLimitEntity> builder)
    {
        builder.ToTable("budget_limits");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ScopeType).HasColumnName("scope_type").IsRequired().HasMaxLength(16);
        builder.Property(e => e.ScopeId).HasColumnName("scope_id").HasColumnType("uuid");
        builder.Property(e => e.DailyBudget).HasColumnName("daily_budget").IsRequired().HasPrecision(18, 6);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Agent / unit rows: scope_id is non-null; uniqueness across
        // (tenant_id, scope_type, scope_id).
        builder.HasIndex(e => new { e.TenantId, e.ScopeType, e.ScopeId })
            .HasDatabaseName("ix_budget_limits_tenant_scope")
            .IsUnique()
            .HasFilter("scope_id IS NOT NULL");

        // Tenant-scope rows: scope_id is null; one row per (tenant_id,
        // scope_type) where scope_type = 'tenant'.
        builder.HasIndex(e => new { e.TenantId, e.ScopeType })
            .HasDatabaseName("ix_budget_limits_tenant_scope_null")
            .IsUnique()
            .HasFilter("scope_id IS NULL");
    }
}
