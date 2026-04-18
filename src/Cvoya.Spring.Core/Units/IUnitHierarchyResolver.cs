// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves ancestor units for a given child unit. Consumed by the
/// hierarchy-aware permission resolver (#414) — a child's effective
/// permission is the strongest grant reachable by walking the parent chain,
/// subject to the boundary opacity rules landed in #413.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the private cloud repo can swap
/// the implementation (e.g. a tenant-scoped lookup backed by a materialized
/// parent index) via DI without touching the permission-service call sites
/// or the unit actor.
/// </para>
/// <para>
/// Unit membership is a DAG (see <see cref="CyclicMembershipException"/>):
/// a single <c>unit://</c> member has exactly one parent today, but the
/// interface returns a list so the resolver can keep working if the
/// invariant is relaxed in a future wave without a contract break.
/// </para>
/// </remarks>
public interface IUnitHierarchyResolver
{
    /// <summary>
    /// Returns every unit that lists <paramref name="child"/> in its members
    /// list. For a well-formed hierarchy this is either empty (root) or a
    /// single entry.
    /// </summary>
    /// <param name="child">
    /// A <c>unit://</c> address. Agent-scheme addresses are not valid — the
    /// permission resolver walks only unit → unit links.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// Zero or more parent unit addresses. An empty result signals "root" —
    /// the caller stops walking.
    /// </returns>
    Task<IReadOnlyList<Address>> GetParentsAsync(Address child, CancellationToken cancellationToken = default);
}