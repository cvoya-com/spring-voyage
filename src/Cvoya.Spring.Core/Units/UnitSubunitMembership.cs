// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Collections.Generic;

/// <summary>
/// Authoritative parent → child unit edge in the unit containment
/// graph (#2052 / ADR-0040). <c>UnitActor</c> reads / writes this row
/// via <see cref="IUnitMemberGraphStore"/> on every member-graph call;
/// there is no actor-state mirror. Top-level units are expressed as an
/// explicit row whose <see cref="ParentId"/> equals the tenant id
/// (the "tenant-root edge"). Cycle detection runs against this table
/// on the membership-coordinator write path so the EF projection is
/// the single source of truth — not a write-through copy of state.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0046 §8 added <c>roles</c> + <c>expertise</c> jsonb columns on
/// the agent-edge <see cref="UnitMembership"/> table. Issue #2463
/// extends the same shape to this sub-unit-edge table so a sub-unit
/// member can advertise the same per-membership team-role + expertise
/// metadata as an agent member (parity on the Unit × Members tab and
/// in <c>sv.directory.list</c>). The fields are runtime metadata only —
/// no platform decision is taken on them.
/// </para>
/// </remarks>
/// <param name="ParentId">
/// The container's stable Guid id. Either the tenant id (top-level
/// units carry a tenant-root edge per #2052) or another unit's id. No
/// DB-level FK because the column is polymorphic; the application
/// enforces validity at write time.
/// </param>
/// <param name="ChildId">The contained unit's stable Guid id.</param>
/// <param name="Roles">
/// ADR-0046 §8 / #2463: free-form team-role strings the sub-unit
/// advertises on the parent unit. Multi-valued; empty list when the
/// manifest omitted the field.
/// </param>
/// <param name="Expertise">
/// ADR-0046 §8 / #2463: free-form expertise tags the sub-unit
/// advertises on the parent unit. Multi-valued; empty list when the
/// manifest omitted the field.
/// </param>
/// <param name="CreatedAt">UTC timestamp when the edge was first persisted.</param>
/// <param name="UpdatedAt">UTC timestamp when the edge was last touched. Equal to <see cref="CreatedAt"/> for non-mutated rows.</param>
public record UnitSubunitMembership(
    Guid ParentId,
    Guid ChildId,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Expertise = null,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default)
{
    /// <summary>Legacy alias for <see cref="ParentId"/>.</summary>
    public Guid ParentUnitId => ParentId;

    /// <summary>Legacy alias for <see cref="ChildId"/>.</summary>
    public Guid ChildUnitId => ChildId;
}
