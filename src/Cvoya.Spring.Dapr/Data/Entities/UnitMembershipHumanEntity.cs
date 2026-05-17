// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one team-membership row for a package-declared human on a unit.
/// Sibling to <see cref="UnitHumanPermissionEntity"/> — the two tables
/// capture orthogonal facts (ADR-0044 § 1): the permissions table carries
/// the tenant operator's platform ACL grants; this table carries the
/// package author's team-role declarations.
///
/// <para>
/// Uniqueness is enforced on <c>(tenant_id, unit_id, human_id, role)</c>
/// via a unique index in the <c>IEntityTypeConfiguration</c>. Multiple
/// rows for the same <c>(unit, human)</c> pair with different roles are
/// legitimate — a single human filling multiple team roles on the same
/// unit. Multiple declarations resolving to the same <c>(human, role)</c>
/// collapse to one row by the unique-index idempotency (ADR-0044 § 3).
/// </para>
/// </summary>
public class UnitMembershipHumanEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key for the membership row.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the unit the row binds to.</summary>
    public Guid UnitId { get; set; }

    /// <summary>Stable Guid identity of the human the row binds to.</summary>
    public Guid HumanId { get; set; }

    /// <summary>
    /// Free-form team role string from the manifest (e.g. <c>owner</c>,
    /// <c>reviewer</c>, <c>security_lead</c>). Never null or whitespace.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Free-form expertise tags carried verbatim from the manifest. Empty
    /// list when the manifest omitted the field; persisted as a jsonb
    /// array column.
    /// </summary>
    public List<string> Expertise { get; set; } = new();

    /// <summary>
    /// Free-form notification event tags carried verbatim from the
    /// manifest. Empty list when the manifest omitted the field;
    /// persisted as a jsonb array column.
    /// </summary>
    public List<string> Notifications { get; set; } = new();

    /// <summary>UTC timestamp when the row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
