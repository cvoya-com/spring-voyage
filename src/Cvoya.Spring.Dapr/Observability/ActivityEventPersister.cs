// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Reactive.Linq;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that subscribes to the <see cref="ActivityEventBus"/> and persists events
/// in 1-second batches to the database via EF Core.
/// </summary>
public sealed class ActivityEventPersister(
    ActivityEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<ActivityEventPersister> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Events
            .Buffer(TimeSpan.FromSeconds(1))
            .Where(batch => batch.Count > 0)
            .Subscribe(
                batch => Task.Run(() => PersistBatchAsync(batch)).GetAwaiter().GetResult(),
                ex => logger.LogError(ex, "ActivityEventPersister stream faulted"));

        logger.LogInformation("ActivityEventPersister started with 1-second batching");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        logger.LogInformation("ActivityEventPersister stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private async Task PersistBatchAsync(IList<ActivityEvent> batch)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            foreach (var activityEvent in batch)
            {
                var record = ActivityEventMapper.ToRecord(activityEvent);
                dbContext.ActivityEvents.Add(record);
            }

            await dbContext.SaveChangesAsync();
            logger.LogDebug("Persisted {Count} activity events", batch.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist batch of {Count} activity events", batch.Count);
        }
    }
}
