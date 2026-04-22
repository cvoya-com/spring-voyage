// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Reactive.Subjects;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// In-process event bus for activity events, backed by an Rx.NET <see cref="Subject{T}"/>.
/// Registered as a singleton so all producers and consumers share a single stream.
/// </summary>
public sealed class ActivityEventBus : IActivityEventBus, IDisposable
{
    private readonly Subject<ActivityEvent> _subject = new();

    /// <summary>
    /// Gets the observable stream of all activity events flowing through the bus.
    /// </summary>
    /// <inheritdoc />
    public IObservable<ActivityEvent> ActivityStream => _subject;

    /// <summary>
    /// Gets the observable stream of all activity events flowing through the bus.
    /// </summary>
    public IObservable<ActivityEvent> Events => _subject;

    /// <summary>
    /// Publishes an activity event to all subscribers.
    /// </summary>
    /// <param name="activityEvent">The event to publish.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activityEvent"/> is <c>null</c>.</exception>
    public void Publish(ActivityEvent activityEvent)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        _subject.OnNext(activityEvent);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activityEvent"/> is <c>null</c>.</exception>
    public Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        cancellationToken.ThrowIfCancellationRequested();
        _subject.OnNext(activityEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _subject.OnCompleted();
        }
        catch (ObjectDisposedException)
        {
            // Subject may already be disposed during shutdown; safe to ignore.
        }

        _subject.Dispose();
    }
}