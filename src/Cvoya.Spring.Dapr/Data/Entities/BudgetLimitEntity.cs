// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Tenant-scoped daily cost budget for a single (scope_type, scope_id) pair.
/// Replaces the pre-ADR-0040 actor-state keys
/// <c>Agent:CostBudget</c>, <c>Unit:CostBudget</c>, and <c>Tenant:CostBudget</c>
/// — see <c>docs/decisions/0040-actor-state-ownership-matrix.md</c>.
///
/// <para>
/// <b>Scope discriminator.</b> <see cref="ScopeType"/> takes one of the
/// canonical values defined on <see cref="BudgetLimitScope"/>
/// (<c>agent</c>, <c>unit</c>, or <c>tenant</c>). For <c>agent</c> and
/// <c>unit</c> rows, <see cref="ScopeId"/> carries the addressable's
/// stable Guid. For <c>tenant</c> rows, <see cref="ScopeId"/> is
/// <c>NULL</c> — there is exactly one tenant-scope row per tenant, and
/// uniqueness is enforced by a partial index in the entity configuration.
/// </para>
/// </summary>
public class BudgetLimitEntity : ITenantScopedEntity
{
    /// <summary>Stable surrogate primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns this budget row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Scope discriminator — one of the canonical values on
    /// <see cref="BudgetLimitScope"/> (<c>agent</c>, <c>unit</c>,
    /// <c>tenant</c>).
    /// </summary>
    public string ScopeType { get; set; } = string.Empty;

    /// <summary>
    /// Stable Guid identity of the scoped addressable. <c>NULL</c> for
    /// tenant-scope rows; required for agent and unit scopes.
    /// </summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Daily cost budget in USD. Always &gt; 0 on persisted rows.</summary>
    public decimal DailyBudget { get; set; }

    /// <summary>UTC timestamp when this row was last upserted.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Canonical string values for <see cref="BudgetLimitEntity.ScopeType"/>.
/// Stored as a string column rather than an enum so adding a new scope is
/// additive (no schema change for an unknown discriminator value at read
/// time) and so the wire / log forms grep cleanly. Compared
/// case-insensitively at write paths.
/// </summary>
public static class BudgetLimitScope
{
    /// <summary>Agent-scoped budget. <c>ScopeId</c> is the agent Guid.</summary>
    public const string Agent = "agent";

    /// <summary>Unit-scoped budget. <c>ScopeId</c> is the unit Guid.</summary>
    public const string Unit = "unit";

    /// <summary>Tenant-scoped budget. <c>ScopeId</c> is <c>NULL</c>.</summary>
    public const string Tenant = "tenant";
}
