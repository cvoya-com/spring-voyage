// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Exposes a unit-scoped activity stream — the merge of the unit's own
/// events with the events emitted by every agent (and transitively every
/// sub-unit) that the unit contains.
/// </summary>
/// <remarks>
/// <para>
/// Physically, every activity event flows through a single process-wide
/// <see cref="IActivityEventBus"/>. A unit-scoped observable is a filtered
/// projection: the set of addresses that belong to the unit (computed at
/// subscribe time) is captured once, and the underlying observable is
/// restricted to events whose <see cref="ActivityEvent.Source"/> falls in
/// that set. This gives <c>Observable.Merge()</c> semantics without
/// requiring multiple hot subjects — closes the
/// "unit aggregates member streams" acceptance item of issue #391.
/// </para>
/// <para>
/// The member set is captured at subscribe time, not re-evaluated per
/// event. Callers that need live membership updates should resubscribe
/// after a membership change.
/// </para>
/// </remarks>
public interface IUnitActivityObservable
{
    /// <summary>
    /// Builds the merged activity stream for <paramref name="unitId"/>.
    /// </summary>
    /// <param name="unitId">The unit's actor id (no scheme prefix).</param>
    /// <param name="cancellationToken">A token used to cancel the subscribe-time member lookup.</param>
    /// <returns>A hot observable scoped to the unit and its descendants.</returns>
    Task<IObservable<ActivityEvent>> GetStreamAsync(string unitId, CancellationToken cancellationToken = default);
}