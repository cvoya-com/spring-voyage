// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Computes the <em>effective</em> expertise of a unit — the union of its
/// own configured domains and every descendant's expertise, walking the
/// member graph down to the leaves. See #412 and
/// <c>docs/architecture/units.md</c> §&nbsp;Expertise Directory Aggregation.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe to call concurrently, must bound the walk
/// with a depth cap that matches the unit membership cycle-detection bound
/// (<c>64</c>), and must surface cycles and over-depth graphs as
/// <see cref="ExpertiseAggregationException"/> — never as an infinite loop or
/// a stack-overflow.
/// </para>
/// <para>
/// Aggregation is cached by unit address. Mutations that may affect the
/// result are signalled with <see cref="InvalidateAsync(Address, CancellationToken)"/>,
/// which invalidates the supplied unit and every ancestor on the path to
/// the root. Callers that add or remove a member, or that edit an agent's or
/// sub-unit's expertise, must invalidate the affected unit so peer-lookup
/// reads see a fresh aggregation on the next call. The invalidation walks
/// <em>up</em> through the <c>ParentUnit</c> pointers maintained by the
/// unit-membership repository — writing to a leaf cascades to every unit
/// that transitively contains it.
/// </para>
/// <para>
/// Boundary projection / filtering / synthesis (#413) is intentionally left
/// as a follow-up extension: an <see cref="IAggregatedExpertiseFilter"/>
/// seam can layer on top of the base aggregator without changing its
/// interface. Today's implementation returns the raw (transparent) view.
/// </para>
/// </remarks>
public interface IExpertiseAggregator
{
    /// <summary>
    /// Returns the effective expertise for <paramref name="unit"/>,
    /// recursively composing its members' expertise all the way down to
    /// the leaves. Results may come from cache; see
    /// <see cref="InvalidateAsync(Address, CancellationToken)"/> for the
    /// propagation contract.
    /// </summary>
    /// <param name="unit">The unit whose effective expertise to compute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="AggregatedExpertise"/> snapshot. A unit that does not
    /// exist in the directory returns an empty snapshot rather than null —
    /// callers can always project the value directly to the wire.
    /// </returns>
    /// <exception cref="ExpertiseAggregationException">
    /// Thrown when the unit graph contains a cycle that reaches back to
    /// <paramref name="unit"/> or when the walk exceeds the maximum safe
    /// depth. The exception carries the path walked so far.
    /// </exception>
    Task<AggregatedExpertise> GetAsync(Address unit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cached aggregation for <paramref name="origin"/> and
    /// every ancestor unit that transitively contains it, so the next
    /// <see cref="GetAsync(Address, CancellationToken)"/> recomputes from
    /// live state. Safe to call on an agent address too — the implementation
    /// looks up the agent's owning units via
    /// <see cref="Units.IUnitMembershipRepository"/> and invalidates each one
    /// (and their ancestors).
    /// </summary>
    /// <param name="origin">
    /// Address whose expertise contribution changed. Pass the agent address
    /// when an agent's expertise is edited; pass the unit address when a
    /// unit adds, removes, or reconfigures a member or its own expertise.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task InvalidateAsync(Address origin, CancellationToken cancellationToken = default);
}