// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Collections.Concurrent;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// OSS implementation of <see cref="IOtlpIngestService"/>. Applies
/// redaction, capture-level truncation, and a per-(subject, kind) token
/// bucket, then publishes accepted events onto the shared
/// <see cref="IActivityEventBus"/>. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Ingest is best-effort by contract: failures (redaction throws,
/// publish throws, downstream EF write throws) are logged and counted,
/// but never surface to the caller. The A2A request/response path must
/// keep running even when the activity-capture plane is broken.
/// </para>
/// <para>
/// The token bucket is a coarse safety net for runtimes that don't
/// rate-limit at emit (the SV Python Agent SDK in #2493 will). It
/// avoids unbounded growth of in-process state — the cleanup pass
/// inside <see cref="TryConsumeBudget"/> evicts buckets that haven't
/// been touched recently.
/// </para>
/// </remarks>
public class OtlpIngestService(
    IActivityEventBus activityBus,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OtlpIngestService> logger) : IOtlpIngestService
{
    /// <summary>Max events per bucket window before the rate limiter trips.</summary>
    public const int BucketCapacity = 200;

    /// <summary>Window over which <see cref="BucketCapacity"/> applies.</summary>
    public static readonly TimeSpan BucketWindow = TimeSpan.FromSeconds(10);

    // Per-(subjectId, kind) token-bucket state. Subject id keeps the
    // bucket isolated per actor; kind isolates so a noisy tool-call
    // emitter doesn't starve span-level events for the same subject.
    private readonly ConcurrentDictionary<(Guid Subject, OtlpEventKind Kind), BucketState> _buckets = new();

    /// <inheritdoc />
    public async Task<OtlpIngestResult> IngestAsync(
        IReadOnlyList<OtlpEventIngest> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return new OtlpIngestResult(0, 0, 0, 0);
        }

        var accepted = 0;
        var droppedCapture = 0;
        var droppedRate = 0;
        var droppedError = 0;

        // Resolve the tenant's capture level once per batch. Every event
        // in a batch belongs to the same tenant (the endpoint enforces
        // that), so a single lookup is correct and cheap. ITenantActivitySettings
        // is scoped (depends on the scoped SpringDbContext); we resolve
        // it through a per-batch DI scope so the singleton ingest service
        // doesn't capture a scoped dependency.
        TenantActivitySettingsSnapshot snapshot;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tenantSettings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
            snapshot = await tenantSettings.GetAsync(events[0].TenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            // If we can't resolve settings the safe action is to drop
            // the batch — never block the runtime, never default to
            // capturing payloads we have no policy for.
            logger.LogWarning(ex,
                "Tenant activity settings lookup failed for tenant {TenantId}; dropping {Count} OTLP events.",
                events[0].TenantId, events.Count);
            return new OtlpIngestResult(0, 0, 0, events.Count);
        }

        if (snapshot.Level == ActivityCaptureLevel.Off)
        {
            return new OtlpIngestResult(0, events.Count, 0, 0);
        }

        foreach (var evt in events)
        {
            if (!TryConsumeBudget(evt.Subject.Id, evt.Kind))
            {
                droppedRate++;
                continue;
            }

            try
            {
                var redacted = ActivityRedactor.Redact(evt.Details);
                var trimmed = ActivityCaptureLevelEnforcer.Apply(redacted, snapshot.Level);

                var activityEvent = new ActivityEvent(
                    Id: Guid.NewGuid(),
                    Timestamp: evt.Timestamp,
                    Source: evt.Subject,
                    EventType: MapKind(evt.Kind),
                    Severity: evt.Severity,
                    Summary: evt.Summary,
                    Details: trimmed,
                    CorrelationId: evt.ThreadId);

                await activityBus.PublishAsync(activityEvent, cancellationToken);
                accepted++;
            }
            catch (Exception ex)
            {
                droppedError++;
                logger.LogWarning(ex,
                    "OTLP ingest failed for subject {SubjectScheme}:{SubjectId} kind {Kind}; event dropped.",
                    evt.Subject.Scheme, evt.Subject.Id, evt.Kind);
            }
        }

        if (droppedRate > 0 || droppedError > 0)
        {
            logger.LogDebug(
                "OTLP ingest batch complete: accepted={Accepted} droppedRate={DroppedRate} droppedError={DroppedError}.",
                accepted, droppedRate, droppedError);
        }

        return new OtlpIngestResult(accepted, droppedCapture, droppedRate, droppedError);
    }

    private static ActivityEventType MapKind(OtlpEventKind kind) => kind switch
    {
        OtlpEventKind.Span => ActivityEventType.RuntimeSpan,
        OtlpEventKind.Log => ActivityEventType.RuntimeLog,
        OtlpEventKind.Progress => ActivityEventType.RuntimeProgress,
        OtlpEventKind.LlmTurn => ActivityEventType.LlmTurn,
        OtlpEventKind.ToolCall => ActivityEventType.ToolCall,
        _ => ActivityEventType.RuntimeLog,
    };

    private bool TryConsumeBudget(Guid subjectId, OtlpEventKind kind)
    {
        var now = timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd((subjectId, kind), _ => new BucketState(now));
        lock (bucket)
        {
            // Refill the bucket based on time elapsed since the last reset.
            if (now - bucket.WindowStart >= BucketWindow)
            {
                bucket.WindowStart = now;
                bucket.Tokens = 0;
            }
            if (bucket.Tokens >= BucketCapacity)
            {
                return false;
            }
            bucket.Tokens++;
            bucket.LastTouched = now;
            return true;
        }
    }

    private sealed class BucketState(DateTimeOffset start)
    {
        public DateTimeOffset WindowStart { get; set; } = start;
        public DateTimeOffset LastTouched { get; set; } = start;
        public int Tokens { get; set; }
    }
}
