// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one parent → child unit edge in the unit containment graph.
/// Mirrors the <c>unit://</c>-scheme entries kept in <c>UnitActor</c>
/// state so the tenant-tree endpoint can render nested unit hierarchies
/// without a per-unit actor fanout (#1154). Per-edge configuration
/// overrides for unit-typed members remain deferred to #217 — this row
/// carries only the edge plus audit timestamps.
/// </summary>
public class UnitSubunitMembershipEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the tenant that owns this edge.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The unit that contains the child (parent unit's <c>Address.Path</c>).</summary>
    public string ParentUnitId { get; set; } = string.Empty;

    /// <summary>The unit being contained (child unit's <c>Address.Path</c>).</summary>
    public string ChildUnitId { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the edge was first projected.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp when the edge was last touched.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}