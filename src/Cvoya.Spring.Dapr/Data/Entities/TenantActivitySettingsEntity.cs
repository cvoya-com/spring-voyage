// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Persistent shape of one tenant's activity-capture settings — the
/// capture level (full / summary / off) and the retention horizon (days).
/// Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Not an <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopedEntity"/> because
/// the table is keyed directly on <see cref="TenantId"/> — a single row per
/// tenant. The cross-tenant retention sweep (which deletes expired rows from
/// <see cref="ActivityEventRecord"/>) reads this table through
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopeBypass"/>; ordinary reads
/// go through the tenant-scoped service which only ever asks for the
/// current tenant's row.
/// </para>
/// </remarks>
public class TenantActivitySettingsEntity
{
    /// <summary>Tenant the row belongs to — primary key.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Capture level. Persisted as the enum name (<c>Off</c> /
    /// <c>Summary</c> / <c>Full</c>) so mid-enum inserts on
    /// <c>ActivityCaptureLevel</c> stay safe.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Retention horizon in days. Must be &gt; 0.</summary>
    public int RetentionDays { get; set; }

    /// <summary>When the row was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
