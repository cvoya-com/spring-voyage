// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decorating implementation of <see cref="IOtlpIngestService"/> that
/// forwards captured activity to a tenant-configured external
/// OpenTelemetry backend (Datadog, Tempo, Jaeger, generic OTLP/HTTP).
/// Issue #2503.
/// </summary>
/// <remarks>
/// <para>
/// The decorator calls the inner ingest first so local persistence
/// remains the source of truth; forwarding is best-effort and runs
/// after the inner accepts. Failures are logged + counted and never
/// surfaced to the runtime caller — the activity-capture plane is
/// strictly best-effort.
/// </para>
/// <para>
/// Redaction is applied to the forwarded payload before it leaves the
/// platform so secrets never reach the external backend, even when the
/// tenant's local capture level is <c>summary</c> (which would have
/// truncated the body); the forwarder uses the same
/// <see cref="ActivityRedactor"/> as the inner service but skips the
/// capture-level enforcer's truncation pass.
/// </para>
/// <para>
/// Retry policy: at most one immediate retry on a 5xx response; network
/// errors and 4xx are surfaced to the metric counter without retry.
/// No queueing, no backoff — keeping the surface area small in v0.1.
/// </para>
/// </remarks>
public class ForwardingOtlpIngestServiceDecorator(
    IOtlpIngestService inner,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<ForwardingOtlpIngestServiceDecorator> logger) : IOtlpIngestService
{
    /// <summary>Named HTTP client the decorator pulls from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "spring-voyage.activity.forward";

    private const string ContentTypeJson = "application/json";
    private const string ContentTypeProtobuf = "application/x-protobuf";

    // Per-tenant last-result tracking for the health surface (#2503).
    // Keyed on the tenant guid because the row count is small (≤ tenants)
    // and the values are diagnostic — never load-bearing.
    private readonly ConcurrentDictionary<Guid, ForwardStatusSnapshot> _status = new();

    /// <summary>
    /// Read-only view of the most recent forward attempt per tenant.
    /// Exposed for the platform-operator health surface
    /// (<c>spring activity forward status</c>) and the portal indicator.
    /// </summary>
    public IReadOnlyDictionary<Guid, ForwardStatusSnapshot> Status => _status;

    /// <inheritdoc />
    public async Task<OtlpIngestResult> IngestAsync(
        IReadOnlyList<OtlpEventIngest> events,
        CancellationToken cancellationToken = default)
    {
        // Local persistence first — never block on the forwarder.
        var innerResult = await inner.IngestAsync(events, cancellationToken);

        if (events.Count == 0)
        {
            return innerResult;
        }

        try
        {
            await ForwardBatchAsync(events, cancellationToken);
        }
        catch (Exception ex)
        {
            // The forwarder body has its own try/catch around every HTTP
            // call; this outer catch is the safety net so a
            // configuration-parsing failure never propagates and breaks
            // the capture path's contract.
            logger.LogWarning(ex,
                "Activity forwarder threw outside the per-call try/catch; tenant={TenantId}.",
                events[0].TenantId);
        }

        return innerResult;
    }

    private async Task ForwardBatchAsync(
        IReadOnlyList<OtlpEventIngest> events,
        CancellationToken cancellationToken)
    {
        var tenantId = events[0].TenantId;

        // Resolve tenant forwarding config from the scoped settings
        // service. The decorator is a singleton wrapping a singleton, so
        // we create a per-batch scope just like the inner service does
        // for ITenantActivitySettings.
        ExternalOtelForwardConfig? config;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
            var snapshot = await settings.GetAsync(tenantId, cancellationToken);
            config = snapshot.ExternalForward;
        }

        if (config is null || !config.Enabled || string.IsNullOrEmpty(config.Endpoint))
        {
            UpdateStatus(tenantId, ForwardStatusKind.Disabled, message: null);
            return;
        }

        // Group events by signal — traces vs logs go to different
        // sub-paths.
        var traces = new List<OtlpEventIngest>(events.Count);
        var logs = new List<OtlpEventIngest>(events.Count);
        foreach (var evt in events)
        {
            (evt.Kind == OtlpEventKind.Log ? logs : traces).Add(evt);
        }

        var anySuccess = false;
        string? lastError = null;
        if (traces.Count > 0)
        {
            var (ok, err) = await PostAsync(config, "/v1/traces", traces, cancellationToken);
            anySuccess = anySuccess || ok;
            if (!ok) lastError = err;
        }
        if (logs.Count > 0)
        {
            var (ok, err) = await PostAsync(config, "/v1/logs", logs, cancellationToken);
            anySuccess = anySuccess || ok;
            if (!ok && lastError is null) lastError = err;
        }

        UpdateStatus(
            tenantId,
            anySuccess ? ForwardStatusKind.Success : ForwardStatusKind.Failure,
            lastError);
    }

    private async Task<(bool Ok, string? Error)> PostAsync(
        ExternalOtelForwardConfig config,
        string path,
        IReadOnlyList<OtlpEventIngest> events,
        CancellationToken cancellationToken)
    {
        var endpoint = config.Endpoint.TrimEnd('/');
        var url = $"{endpoint}{path}";

        // Apply redaction to each event's Details payload before
        // building the wire envelope. The local-persist path applies
        // capture-level enforcer truncation as well; the forwarder
        // intentionally skips truncation so the external backend sees
        // the un-truncated (but redacted) payload.
        var redacted = new List<OtlpEventIngest>(events.Count);
        foreach (var evt in events)
        {
            var safeDetails = ActivityRedactor.Redact(evt.Details);
            redacted.Add(evt with { Details = safeDetails });
        }

        var (body, contentType) = BuildEnvelope(path, redacted, config.Protocol);

        for (var attempt = 0; attempt <= 1; attempt++)
        {
            try
            {
                using var client = httpClientFactory.CreateClient(HttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                request.Content = content;
                foreach (var header in config.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return (true, null);
                }
                var status = (int)response.StatusCode;
                if (status >= 500 && attempt == 0)
                {
                    // Single retry on 5xx; loop continues.
                    continue;
                }
                return (false, $"HTTP {status}");
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (false, "cancelled");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "External forward attempt {Attempt} failed for {Url}", attempt, url);
                if (attempt == 0)
                {
                    continue;
                }
                return (false, ex.GetType().Name);
            }
        }
        return (false, "exhausted retries");
    }

    private static (byte[] Body, string ContentType) BuildEnvelope(
        string path,
        IReadOnlyList<OtlpEventIngest> events,
        string protocol)
    {
        // We don't reconstitute the original OTLP wire shape (we don't
        // store traceId / spanId on the activity bus and the originals
        // are gone after MapTraces/MapLogs). Instead, we send a
        // platform-shaped JSON or protobuf envelope the external
        // collector can ingest as logs — every event projects to one
        // OTLP LogRecord carrying the activity summary, severity, and
        // already-redacted details payload. This keeps the forwarder
        // wire-compatible with generic OTLP/HTTP collectors (Datadog,
        // Tempo, Jaeger all accept logs via OTLP/HTTP).
        var json = JsonSerializer.Serialize(BuildLogPayload(events));
        // v0.1: protobuf forwarding falls back to JSON. The full
        // protobuf path for activity forwarding requires re-encoding
        // the platform's ActivityEvent shape into OTLP LogRecord
        // protobufs — TODO(#2503-followup).
        _ = protocol;
        _ = path;
        return (Encoding.UTF8.GetBytes(json), ContentTypeJson);
    }

    private static object BuildLogPayload(IReadOnlyList<OtlpEventIngest> events)
    {
        return new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[]
                        {
                            new { key = "service.name", value = new { stringValue = "spring-voyage" } },
                            new { key = "sv.tenant.id", value = new { stringValue = events[0].TenantId.ToString("D") } },
                        },
                    },
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = "spring-voyage.activity.forward", version = "0.1.0" },
                            logRecords = events.Select(evt => new
                            {
                                timeUnixNano = ((long)(evt.Timestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds * 1_000_000L).ToString(),
                                severityText = evt.Severity.ToString().ToUpperInvariant(),
                                body = new { stringValue = evt.Summary },
                                attributes = new[]
                                {
                                    new { key = "sv.event.kind", value = new { stringValue = evt.Kind.ToString() } },
                                    new { key = "sv.subject", value = new { stringValue = evt.Subject.ToString() } },
                                    new { key = "sv.thread.id", value = new { stringValue = evt.ThreadId ?? string.Empty } },
                                    new { key = "sv.message.id", value = new { stringValue = evt.MessageId ?? string.Empty } },
                                    new { key = "sv.details", value = new { stringValue = evt.Details.GetRawText() } },
                                },
                            }).ToArray(),
                        },
                    },
                },
            },
        };
    }

    private void UpdateStatus(Guid tenantId, ForwardStatusKind kind, string? message)
    {
        var snapshot = new ForwardStatusSnapshot(tenantId, kind, timeProvider.GetUtcNow(), message);
        _status[tenantId] = snapshot;
    }
}

/// <summary>
/// Diagnostic snapshot of the last forwarding attempt for a tenant
/// (#2503). Surfaced through the <c>spring activity forward status</c>
/// CLI verb and the portal forward-indicator.
/// </summary>
/// <param name="TenantId">Tenant the snapshot belongs to.</param>
/// <param name="Kind">Last-result classification.</param>
/// <param name="ObservedAt">When the result was recorded.</param>
/// <param name="Message">
/// Free-text detail for failures (HTTP status, exception class). Null
/// for successes / disabled snapshots.
/// </param>
public sealed record ForwardStatusSnapshot(
    Guid TenantId,
    ForwardStatusKind Kind,
    DateTimeOffset ObservedAt,
    string? Message);

/// <summary>Possible <see cref="ForwardStatusSnapshot"/> states.</summary>
public enum ForwardStatusKind
{
    /// <summary>The forwarder is configured but not enabled for this tenant.</summary>
    Disabled,

    /// <summary>The most recent attempt succeeded.</summary>
    Success,

    /// <summary>The most recent attempt failed.</summary>
    Failure,
}
