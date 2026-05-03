// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using System.Reactive.Linq;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that subscribes to the <see cref="ActivityEventBus"/> for <see cref="ActivityEventType.CostIncurred"/>
/// events and persists <see cref="CostRecord"/> entities in 1-second batches to the database via EF Core.
/// </summary>
public sealed partial class CostTracker(
    ActivityEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<CostTracker> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Events
            .Where(e => e.EventType == ActivityEventType.CostIncurred)
            .Buffer(TimeSpan.FromSeconds(1))
            .Where(batch => batch.Count > 0)
            .Subscribe(
                batch => Task.Run(() => PersistBatchAsync(batch)).GetAwaiter().GetResult(),
                ex => LogStreamFaulted(logger, ex));

        LogStarted(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        LogStopped(logger);
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
                var record = MapToRecord(activityEvent);
                if (record is not null)
                {
                    dbContext.CostRecords.Add(record);
                }
            }

            await dbContext.SaveChangesAsync();
            LogPersistedBatch(logger, batch.Count);
        }
        catch (Exception ex)
        {
            LogPersistFailed(logger, batch.Count, ex);
        }
    }

    internal static CostRecord? MapToRecord(ActivityEvent activityEvent)
    {
        var details = activityEvent.Details;
        if (details is null || details.Value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var json = details.Value;

        var tenantIdString = GetStringProperty(json, "tenantId");
        var tenantId = Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(tenantIdString, out var t)
            ? t
            : Cvoya.Spring.Core.Tenancy.OssTenantIds.Default;

        var unitIdString = GetStringProperty(json, "unitId");
        Guid? unitId = Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitIdString, out var u)
            ? u
            : null;

        return new CostRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgentId = activityEvent.Source.Id,
            UnitId = unitId,
            Model = GetStringProperty(json, "model") ?? "unknown",
            InputTokens = GetIntProperty(json, "inputTokens"),
            OutputTokens = GetIntProperty(json, "outputTokens"),
            Cost = activityEvent.Cost ?? 0m,
            Duration = GetDurationProperty(json, "durationMs"),
            Timestamp = activityEvent.Timestamp,
            CorrelationId = activityEvent.CorrelationId,
            Source = GetCostSourceProperty(json),
        };
    }

    private static CostSource GetCostSourceProperty(JsonElement json)
    {
        // Accept either a string ("Work"/"Initiative", case-insensitive) or a
        // numeric ordinal. Anything unrecognised falls back to Work so a
        // typo at the emission site can't silently reclassify costs.
        if (!json.TryGetProperty("costSource", out var prop))
        {
            return CostSource.Work;
        }

        switch (prop.ValueKind)
        {
            case JsonValueKind.String:
                return Enum.TryParse<CostSource>(prop.GetString(), ignoreCase: true, out var parsed)
                    ? parsed
                    : CostSource.Work;

            case JsonValueKind.Number:
                return Enum.IsDefined(typeof(CostSource), prop.GetInt32())
                    ? (CostSource)prop.GetInt32()
                    : CostSource.Work;

            default:
                return CostSource.Work;
        }
    }

    private static string? GetStringProperty(JsonElement json, string propertyName)
    {
        return json.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int GetIntProperty(JsonElement json, string propertyName)
    {
        return json.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : 0;
    }

    private static TimeSpan? GetDurationProperty(JsonElement json, string propertyName)
    {
        if (json.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return TimeSpan.FromMilliseconds(prop.GetDouble());
        }

        return null;
    }

    [LoggerMessage(EventId = 2300, Level = LogLevel.Information, Message = "CostTracker started with 1-second batching")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "CostTracker stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Debug, Message = "Persisted {Count} cost records")]
    private static partial void LogPersistedBatch(ILogger logger, int count);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Error, Message = "Failed to persist batch of {Count} cost records")]
    private static partial void LogPersistFailed(ILogger logger, int count, Exception exception);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Error, Message = "CostTracker stream faulted")]
    private static partial void LogStreamFaulted(ILogger logger, Exception exception);
}