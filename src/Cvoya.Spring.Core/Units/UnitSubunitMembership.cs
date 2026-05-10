// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

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
/// Per #217, the related <see cref="UnitMembership"/> table is
/// agent-scheme-only and carries per-membership configuration overrides
/// (model, specialty, execution mode). Unit-typed members do not yet
/// support per-edge configuration; this entity is intentionally minimal
/// — just the edge plus audit timestamps — so #217 can extend it (or
/// replace it with a polymorphic shared table) without churning
/// another migration.
/// </para>
/// </remarks>
/// <param name="ParentId">
/// The container's stable Guid id. Either the tenant id (top-level
/// units carry a tenant-root edge per #2052) or another unit's id. No
/// DB-level FK because the column is polymorphic; the application
/// enforces validity at write time.
/// </param>
/// <param name="ChildId">The contained unit's stable Guid id.</param>
/// <param name="CreatedAt">UTC timestamp when the edge was first persisted.</param>
/// <param name="UpdatedAt">UTC timestamp when the edge was last touched. Equal to <see cref="CreatedAt"/> for non-mutated rows.</param>
public record UnitSubunitMembership(
    Guid ParentId,
    Guid ChildId,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default)
{
    /// <summary>Legacy alias for <see cref="ParentId"/>.</summary>
    public Guid ParentUnitId => ParentId;

    /// <summary>Legacy alias for <see cref="ChildId"/>.</summary>
    public Guid ChildUnitId => ChildId;
}
