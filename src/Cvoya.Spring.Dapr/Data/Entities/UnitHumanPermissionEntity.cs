// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Persists one (unit, human) permission grant. Replaces the actor-state
/// <c>Unit:HumanPermissions</c> map (and the redundant
/// <c>Human:UnitPermissions</c> dual view) with a tenant-scoped EF row so
/// authorization reads become a single indexed SQL lookup instead of an
/// O(depth) walk of activated unit actors.
///
/// <para>
/// Implements ADR-0040 for the ACL slice. Uniqueness is enforced on
/// <c>(tenant_id, unit_id, human_id)</c> via a unique index in the
/// <c>IEntityTypeConfiguration</c> — there is exactly one direct grant per
/// (unit, human) pair within a tenant.
/// </para>
/// </summary>
public class UnitHumanPermissionEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key for the row.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the unit the grant is recorded against.</summary>
    public Guid UnitId { get; set; }

    /// <summary>Stable Guid identity of the human the grant applies to (#1491).</summary>
    public Guid HumanId { get; set; }

    /// <summary>The permission level granted to the human within the unit.</summary>
    public PermissionLevel PermissionLevel { get; set; }

    /// <summary>
    /// Optional human-readable identity captured at grant time. Mirrors the
    /// pre-EF <see cref="UnitPermissionEntry.Identity"/> field; preserved so
    /// the unit-listing surface (<c>GET /api/v1/units/{id}/humans</c>) keeps
    /// the same shape after the move from actor state.
    /// </summary>
    public string? Identity { get; set; }

    /// <summary>
    /// Whether this human receives notifications from the unit. Mirrors the
    /// pre-EF <see cref="UnitPermissionEntry.Notifications"/> field; default
    /// is <c>true</c> on insert.
    /// </summary>
    public bool Notifications { get; set; } = true;

    /// <summary>UTC timestamp when the grant was first created.</summary>
    public DateTimeOffset GrantedAt { get; set; }
}
