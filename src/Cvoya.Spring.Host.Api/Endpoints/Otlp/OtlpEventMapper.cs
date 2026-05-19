// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints.Otlp;

using System.Globalization;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Converts inbound OTLP/HTTP+JSON batches into normalised
/// <see cref="OtlpEventIngest"/> records the ingest service publishes
/// onto the activity bus. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Resource-attribute mismatches (a span whose <c>sv.tenant.id</c> /
/// <c>sv.subject.uuid</c> does not match the authenticated principal)
/// are silently filtered out. Best-effort capture: a misbehaving
/// runtime stamping mismatched resource attributes cannot reach
/// another tenant's activity bus.
/// </para>
/// </remarks>
public static class OtlpEventMapper
{
    private const string SvProgressEventName = "sv.progress";
    private const string SvLlmTurnSpanName = "sv.llm.turn";
    private const string SvToolCallSpanName = "sv.tool.call";

    /// <summary>Maps an <see cref="OtlpLogsRequest"/> into the normalised event list.</summary>
    public static IReadOnlyList<OtlpEventIngest> MapLogs(
        OtlpLogsRequest request,
        Guid tenantId,
        Address subjectAddress,
        ILogger logger)
    {
        if (request.ResourceLogs is null || request.ResourceLogs.Count == 0)
        {
            return Array.Empty<OtlpEventIngest>();
        }

        var output = new List<OtlpEventIngest>();
        foreach (var resourceLogs in request.ResourceLogs)
        {
            if (!OtlpIngestEndpoints.ResourceMatchesPrincipal(
                    resourceLogs.Resource, tenantId, subjectAddress))
            {
                logger.LogDebug(
                    "OTLP log batch resource-attribute mismatch; dropping {Count} scopeLogs.",
                    resourceLogs.ScopeLogs.Count);
                continue;
            }

            foreach (var scope in resourceLogs.ScopeLogs)
            {
                foreach (var record in scope.LogRecords)
                {
                    output.Add(MapLogRecord(record, resourceLogs.Resource!, tenantId, subjectAddress));
                }
            }
        }
        return output;
    }

    /// <summary>Maps an <see cref="OtlpTracesRequest"/> into the normalised event list.</summary>
    public static IReadOnlyList<OtlpEventIngest> MapTraces(
        OtlpTracesRequest request,
        Guid tenantId,
        Address subjectAddress,
        ILogger logger)
    {
        if (request.ResourceSpans is null || request.ResourceSpans.Count == 0)
        {
            return Array.Empty<OtlpEventIngest>();
        }

        var output = new List<OtlpEventIngest>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            if (!OtlpIngestEndpoints.ResourceMatchesPrincipal(
                    resourceSpans.Resource, tenantId, subjectAddress))
            {
                logger.LogDebug(
                    "OTLP trace batch resource-attribute mismatch; dropping {Count} scopeSpans.",
                    resourceSpans.ScopeSpans.Count);
                continue;
            }

            foreach (var scope in resourceSpans.ScopeSpans)
            {
                foreach (var span in scope.Spans)
                {
                    output.Add(MapSpan(span, resourceSpans.Resource!, tenantId, subjectAddress));
                    foreach (var ev in span.Events)
                    {
                        if (string.Equals(ev.Name, SvProgressEventName, StringComparison.Ordinal))
                        {
                            output.Add(MapProgressEvent(ev, span, resourceSpans.Resource!, tenantId, subjectAddress));
                        }
                    }
                }
            }
        }
        return output;
    }

    private static OtlpEventIngest MapLogRecord(
        OtlpLogRecord record,
        OtlpResource resource,
        Guid tenantId,
        Address subject)
    {
        var attributes = AttributesToDictionary(record.Attributes);
        attributes["sv.resource"] = ResourceToJsonNode(resource);
        if (record.Body?.StringValue is { } bodyString)
        {
            attributes["body"] = bodyString;
        }
        else if (record.Body is not null)
        {
            attributes["body"] = AnyValueToString(record.Body);
        }

        var severity = MapLogSeverity(record.SeverityText, record.SeverityNumber);
        var summary = ComposeSummary(
            "log",
            record.SeverityText ?? severity.ToString(),
            record.Body?.StringValue);

        return new OtlpEventIngest(
            Kind: OtlpEventKind.Log,
            Subject: subject,
            TenantId: tenantId,
            ThreadId: ExtractStringAttribute(resource, OtelResourceKeys.ThreadId)
                ?? ExtractStringAttribute(record.Attributes, OtelResourceKeys.ThreadId),
            MessageId: ExtractStringAttribute(resource, OtelResourceKeys.MessageId)
                ?? ExtractStringAttribute(record.Attributes, OtelResourceKeys.MessageId),
            Timestamp: ParseUnixNanos(record.TimeUnixNano),
            Summary: summary,
            Severity: severity,
            Details: JsonSerializer.SerializeToElement(attributes));
    }

    private static OtlpEventIngest MapSpan(
        OtlpSpan span,
        OtlpResource resource,
        Guid tenantId,
        Address subject)
    {
        var attributes = AttributesToDictionary(span.Attributes);
        attributes["sv.resource"] = ResourceToJsonNode(resource);
        attributes["span.name"] = span.Name ?? string.Empty;
        if (span.TraceId is not null) attributes["trace.id"] = span.TraceId;
        if (span.SpanId is not null) attributes["span.id"] = span.SpanId;
        if (span.ParentSpanId is not null) attributes["span.parent_id"] = span.ParentSpanId;
        if (span.Status is { } status)
        {
            attributes["span.status.code"] = status.Code ?? 0;
            if (!string.IsNullOrEmpty(status.Message))
            {
                attributes["span.status.message"] = status.Message;
            }
        }

        var kind = string.Equals(span.Name, SvLlmTurnSpanName, StringComparison.Ordinal)
            ? OtlpEventKind.LlmTurn
            : string.Equals(span.Name, SvToolCallSpanName, StringComparison.Ordinal)
                ? OtlpEventKind.ToolCall
                : OtlpEventKind.Span;

        var severity = span.Status?.Code == 2 ? ActivitySeverity.Error : ActivitySeverity.Info;

        return new OtlpEventIngest(
            Kind: kind,
            Subject: subject,
            TenantId: tenantId,
            ThreadId: ExtractStringAttribute(resource, OtelResourceKeys.ThreadId)
                ?? ExtractStringAttribute(span.Attributes, OtelResourceKeys.ThreadId),
            MessageId: ExtractStringAttribute(resource, OtelResourceKeys.MessageId)
                ?? ExtractStringAttribute(span.Attributes, OtelResourceKeys.MessageId),
            Timestamp: ParseUnixNanos(span.StartTimeUnixNano),
            Summary: span.Name ?? "(unnamed span)",
            Severity: severity,
            Details: JsonSerializer.SerializeToElement(attributes));
    }

    private static OtlpEventIngest MapProgressEvent(
        OtlpSpanEvent ev,
        OtlpSpan parentSpan,
        OtlpResource resource,
        Guid tenantId,
        Address subject)
    {
        var attributes = AttributesToDictionary(ev.Attributes);
        attributes["sv.resource"] = ResourceToJsonNode(resource);
        attributes["parent.span.id"] = parentSpan.SpanId ?? string.Empty;

        var message = ExtractStringAttribute(ev.Attributes, "message") ?? ev.Name ?? "(progress)";

        return new OtlpEventIngest(
            Kind: OtlpEventKind.Progress,
            Subject: subject,
            TenantId: tenantId,
            ThreadId: ExtractStringAttribute(resource, OtelResourceKeys.ThreadId),
            MessageId: ExtractStringAttribute(resource, OtelResourceKeys.MessageId),
            Timestamp: ParseUnixNanos(ev.TimeUnixNano),
            Summary: message,
            Severity: ActivitySeverity.Info,
            Details: JsonSerializer.SerializeToElement(attributes));
    }

    private static Dictionary<string, object?> AttributesToDictionary(List<OtlpKeyValue> attributes)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (attributes is null) return dict;
        foreach (var kv in attributes)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            dict[kv.Key] = AnyValueToObject(kv.Value);
        }
        return dict;
    }

    private static Dictionary<string, object?> ResourceToJsonNode(OtlpResource resource)
        => resource.Attributes is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : AttributesToDictionary(resource.Attributes);

    private static object? AnyValueToObject(OtlpAnyValue? value)
    {
        if (value is null) return null;
        if (value.StringValue is not null) return value.StringValue;
        if (value.BoolValue.HasValue) return value.BoolValue.Value;
        if (value.IntValue is not null && long.TryParse(value.IntValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }
        if (value.DoubleValue.HasValue) return value.DoubleValue.Value;
        if (value.ArrayValue is not null)
        {
            return value.ArrayValue.Values.Select(AnyValueToObject).ToArray();
        }
        if (value.KvlistValue is not null)
        {
            return AttributesToDictionary(value.KvlistValue.Values);
        }
        return null;
    }

    private static string AnyValueToString(OtlpAnyValue value)
        => AnyValueToObject(value)?.ToString() ?? string.Empty;

    private static string? ExtractStringAttribute(OtlpResource? resource, string key)
        => resource?.Attributes?.FirstOrDefault(a => a.Key == key)?.Value?.StringValue;

    private static string? ExtractStringAttribute(List<OtlpKeyValue>? attributes, string key)
        => attributes?.FirstOrDefault(a => a.Key == key)?.Value?.StringValue;

    private static ActivitySeverity MapLogSeverity(string? severityText, int? severityNumber)
    {
        if (severityNumber.HasValue)
        {
            // OTLP severity number scheme: TRACE=1..4, DEBUG=5..8, INFO=9..12,
            // WARN=13..16, ERROR=17..20, FATAL=21..24.
            return severityNumber.Value switch
            {
                <= 4 => ActivitySeverity.Debug,
                <= 8 => ActivitySeverity.Debug,
                <= 12 => ActivitySeverity.Info,
                <= 16 => ActivitySeverity.Warning,
                _ => ActivitySeverity.Error,
            };
        }
        if (!string.IsNullOrEmpty(severityText))
        {
            return severityText.ToUpperInvariant() switch
            {
                "TRACE" or "DEBUG" => ActivitySeverity.Debug,
                "INFO" or "INFORMATION" => ActivitySeverity.Info,
                "WARN" or "WARNING" => ActivitySeverity.Warning,
                "ERROR" or "FATAL" or "CRITICAL" => ActivitySeverity.Error,
                _ => ActivitySeverity.Info,
            };
        }
        return ActivitySeverity.Info;
    }

    private static string ComposeSummary(string prefix, string severity, string? body)
    {
        var trimmedBody = body is null
            ? string.Empty
            : body.Length > 256 ? body[..256] + "…" : body;
        return string.IsNullOrEmpty(trimmedBody)
            ? $"[{prefix}/{severity}]"
            : $"[{prefix}/{severity}] {trimmedBody}";
    }

    private static DateTimeOffset ParseUnixNanos(string? value)
    {
        if (string.IsNullOrEmpty(value) || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            return DateTimeOffset.UtcNow;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
    }
}
