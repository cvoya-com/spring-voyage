// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Indicates that a component exposes an observable stream of
/// <see cref="ActivityEvent"/> values. The contract is intentionally minimal
/// so it can be implemented by anything that produces events — actors,
/// connectors, aggregated views (e.g., a unit that merges its members'
/// streams). Consumers compose Rx.NET operators
/// (<c>Where</c>, <c>Buffer</c>, <c>Merge</c>, <c>Throttle</c>) on
/// <see cref="ActivityStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every implementation must expose a hot <see cref="IObservable{T}"/> —
/// subscribers see events published after they subscribe, not historical
/// events. <see cref="IActivityQueryService"/> in
/// <c>Cvoya.Spring.Core.Observability</c> fills the historical-query gap.
/// </para>
/// <para>
/// <see cref="IActivityEventBus"/> is the platform-wide hot stream; per-unit
/// and per-agent observables are filtered projections layered on top of it.
/// </para>
/// </remarks>
public interface IActivityObservable
{
    /// <summary>
    /// Gets the hot stream of activity events produced by this component.
    /// </summary>
    IObservable<ActivityEvent> ActivityStream { get; }
}