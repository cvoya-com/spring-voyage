// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one parent → child unit edge in the unit containment graph.
/// Composite primary key on <c>(tenant_id, parent_id, child_id)</c>; no
/// slug column. <see cref="ParentId"/> may be a tenant id (top-level
/// units) or a unit id — there is no DB-level FK on the parent column
/// because the reference is polymorphic; the application enforces a
/// real tenant or unit at write time.
/// </summary>
public class UnitSubunitMembershipEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the tenant that owns this edge.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The container's stable Guid id. Tenant id for top-level units;
    /// otherwise a unit id. Polymorphic — no DB FK.
    /// </summary>
    public Guid ParentId { get; set; }

    /// <summary>The contained unit's stable Guid id.</summary>
    public Guid ChildId { get; set; }

    /// <summary>UTC timestamp when the edge was first projected.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp when the edge was last touched.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Legacy alias for <see cref="ParentId"/>.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Guid ParentUnitId
    {
        get => ParentId;
        set => ParentId = value;
    }

    /// <summary>Legacy alias for <see cref="ChildId"/>.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Guid ChildUnitId
    {
        get => ChildId;
        set => ChildId = value;
    }
}
