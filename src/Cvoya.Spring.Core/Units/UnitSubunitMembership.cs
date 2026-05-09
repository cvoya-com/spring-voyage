// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Persistent projection of one parent → child unit edge in the unit
/// containment graph. Mirrors the actor-state list maintained by
/// <c>UnitActor</c> for <c>unit://</c>-scheme members so readers (the
/// <c>/api/v1/tenant/tree</c> endpoint, future analytics, the cloud
/// overlay) can render nested unit hierarchies without a per-unit actor
/// fanout. The actor-state list remains authoritative for runtime
/// dispatch; this projection is best-effort write-through and is
/// reconciled on host startup if it ever drifts (#1154).
/// </summary>
/// <remarks>
/// <para>
/// Per #217, the existing <see cref="UnitMembership"/> table is
/// agent-scheme-only and carries per-membership configuration overrides
/// (model, specialty, execution mode). Unit-typed members do not yet
/// support per-edge configuration; the receive-time consult in
/// <c>UnitActor.HandleDomainMessageAsync</c> is unchanged. This entity
/// is intentionally minimal — just the edge plus audit timestamps — so
/// #217 can extend it (or replace it with a polymorphic shared table)
/// without churning another migration.
/// </para>
/// <para>
/// Cycle detection lives on <c>UnitActor.AddMemberAsync</c>; this record
/// is never the source of truth for "would this add create a cycle".
/// </para>
/// </remarks>
/// <param name="ParentId">
/// The container's stable Guid id. May be the tenant id (top-level units)
/// or another unit's id. No DB-level FK because the column is polymorphic;
/// the application enforces validity at write time.
/// </param>
/// <param name="ChildId">The contained unit's stable Guid id.</param>
/// <param name="CreatedAt">UTC timestamp when the edge was first projected.</param>
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
