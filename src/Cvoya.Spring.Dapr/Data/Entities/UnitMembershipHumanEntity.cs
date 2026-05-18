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
/// ADR-0046 §7 collapses the natural key to <c>(tenant_id, unit_id, human_id)</c>
/// — the unique index is enforced in the <c>IEntityTypeConfiguration</c>.
/// <see cref="Roles"/> is now a multi-valued jsonb list on the row itself
/// (replacing the per-row <c>role</c> column from ADR-0044 § 3). One row per
/// participant; a human filling multiple team roles surfaces as one row
/// whose <c>roles</c> list carries every role label.
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
    /// Free-form team-role strings carried verbatim from the manifest
    /// (ADR-0046 §3). Persisted as a jsonb array column. Empty list when
    /// the manifest omitted the field.
    /// </summary>
    public List<string> Roles { get; set; } = new();

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
