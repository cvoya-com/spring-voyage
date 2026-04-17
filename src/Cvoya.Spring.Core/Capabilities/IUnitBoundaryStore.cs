// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Persistence seam for a unit's declared <see cref="UnitBoundary"/>. Keeping
/// the store abstract (instead of reading directly off actor state) lets the
/// boundary decorator (#413) resolve a unit's rules uniformly, and lets the
/// private cloud repo swap in a tenant-scoped or cached implementation
/// without changing the decorator.
/// </summary>
/// <remarks>
/// <para>
/// The default OSS implementation reads and writes the boundary record via
/// the <c>UnitActor</c> — the same actor-owned state pattern used for unit
/// metadata, connector binding, and own expertise. A unit with no boundary
/// configured returns <see cref="UnitBoundary.Empty"/> (never <c>null</c>)
/// so callers never need to branch on "has a row".
/// </para>
/// <para>
/// Writes are mostly rare (boundary configuration is relatively static), so
/// the store does not expose a batch API. A future event-driven variant can
/// sit behind this seam without changing call sites.
/// </para>
/// </remarks>
public interface IUnitBoundaryStore
{
    /// <summary>
    /// Reads the boundary configuration for the supplied unit. Returns
    /// <see cref="UnitBoundary.Empty"/> when the unit has no configuration
    /// or when the address does not resolve to a known unit.
    /// </summary>
    /// <param name="unit">A unit address.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<UnitBoundary> GetAsync(
        Address unit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the boundary configuration for the supplied unit. Passing
    /// <see cref="UnitBoundary.Empty"/> is a valid "clear all boundary
    /// rules" operation — implementations are free to represent that as a
    /// delete so a unit with no boundary rules does not occupy a state row.
    /// </summary>
    /// <param name="unit">A unit address.</param>
    /// <param name="boundary">The boundary configuration to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync(
        Address unit,
        UnitBoundary boundary,
        CancellationToken cancellationToken = default);
}