// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Provides a mechanism for emitting activity events that can be observed by subscribers.
/// Implementations bridge the gap between event producers (actors, execution dispatchers)
/// and the observation infrastructure (pub/sub, dashboards).
/// </summary>
public interface IActivityEventBus
{
    /// <summary>
    /// Gets the observable stream of activity events flowing through the bus.
    /// </summary>
    IObservable<ActivityEvent> ActivityStream { get; }

    /// <summary>
    /// Publishes an activity event to all registered subscribers.
    /// </summary>
    /// <param name="activityEvent">The activity event to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous publish operation.</returns>
    Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default);
}