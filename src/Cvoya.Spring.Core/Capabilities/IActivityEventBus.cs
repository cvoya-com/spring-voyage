// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Provides a mechanism for emitting activity events that can be observed by subscribers.
/// Implementations bridge the gap between event producers (actors, execution dispatchers)
/// and the observation infrastructure (pub/sub, dashboards).
/// </summary>
/// <remarks>
/// Implementations of <see cref="IActivityObservable"/> expose the platform-wide
/// hot stream. Per-unit and per-agent projections (e.g.,
/// <c>IUnitActivityObservable</c>) are built by filtering / merging this stream,
/// not by maintaining separate subjects — a single bus keeps the ordering
/// guarantees simple and lets every consumer (SSE relay, cost aggregator,
/// budget enforcer, event persister) share the same event flow.
/// </remarks>
public interface IActivityEventBus : IActivityObservable
{
    /// <summary>
    /// Publishes an activity event to all registered subscribers.
    /// </summary>
    /// <param name="activityEvent">The activity event to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous publish operation.</returns>
    Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default);
}