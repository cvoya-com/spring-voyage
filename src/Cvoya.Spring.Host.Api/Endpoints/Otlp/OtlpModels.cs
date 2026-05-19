// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints.Otlp;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Minimal subset of the OTLP/HTTP+JSON wire format the platform consumes
/// at <c>/otlp/v1/logs</c> and <c>/otlp/v1/traces</c>. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Only the fields the activity-capture plane actually reads are modelled.
/// Unknown fields on the wire are tolerated (System.Text.Json's default
/// is "ignore unknown properties"), so a producer that sets richer
/// attributes will not be rejected. Promoting more fields to typed
/// properties is a backwards-compatible additive change.
/// </para>
/// <para>
/// <c>protobuf</c> support is deferred to a follow-up (#2502). The
/// launcher pins <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/json</c> so the
/// SDK in #2493 emits the JSON form natively.
/// </para>
/// </remarks>
public sealed class OtlpLogsRequest
{
    [JsonPropertyName("resourceLogs")]
    public List<OtlpResourceLogs> ResourceLogs { get; set; } = new();
}

public sealed class OtlpResourceLogs
{
    [JsonPropertyName("resource")]
    public OtlpResource? Resource { get; set; }

    [JsonPropertyName("scopeLogs")]
    public List<OtlpScopeLogs> ScopeLogs { get; set; } = new();
}

public sealed class OtlpScopeLogs
{
    [JsonPropertyName("logRecords")]
    public List<OtlpLogRecord> LogRecords { get; set; } = new();
}

public sealed class OtlpLogRecord
{
    /// <summary>Unix nanos timestamp of the observed event. Optional.</summary>
    [JsonPropertyName("timeUnixNano")]
    public string? TimeUnixNano { get; set; }

    /// <summary>OTLP severity text — INFO / WARN / ERROR / etc. Optional.</summary>
    [JsonPropertyName("severityText")]
    public string? SeverityText { get; set; }

    /// <summary>OTLP severity number (1-24). Optional.</summary>
    [JsonPropertyName("severityNumber")]
    public int? SeverityNumber { get; set; }

    /// <summary>Log body — either a plain string or a structured value.</summary>
    [JsonPropertyName("body")]
    public OtlpAnyValue? Body { get; set; }

    [JsonPropertyName("attributes")]
    public List<OtlpKeyValue> Attributes { get; set; } = new();

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }
}

public sealed class OtlpTracesRequest
{
    [JsonPropertyName("resourceSpans")]
    public List<OtlpResourceSpans> ResourceSpans { get; set; } = new();
}

public sealed class OtlpResourceSpans
{
    [JsonPropertyName("resource")]
    public OtlpResource? Resource { get; set; }

    [JsonPropertyName("scopeSpans")]
    public List<OtlpScopeSpans> ScopeSpans { get; set; } = new();
}

public sealed class OtlpScopeSpans
{
    [JsonPropertyName("spans")]
    public List<OtlpSpan> Spans { get; set; } = new();
}

public sealed class OtlpSpan
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public int? Kind { get; set; }

    [JsonPropertyName("startTimeUnixNano")]
    public string? StartTimeUnixNano { get; set; }

    [JsonPropertyName("endTimeUnixNano")]
    public string? EndTimeUnixNano { get; set; }

    [JsonPropertyName("attributes")]
    public List<OtlpKeyValue> Attributes { get; set; } = new();

    [JsonPropertyName("events")]
    public List<OtlpSpanEvent> Events { get; set; } = new();

    [JsonPropertyName("status")]
    public OtlpSpanStatus? Status { get; set; }
}

public sealed class OtlpSpanEvent
{
    [JsonPropertyName("timeUnixNano")]
    public string? TimeUnixNano { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("attributes")]
    public List<OtlpKeyValue> Attributes { get; set; } = new();
}

public sealed class OtlpSpanStatus
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

public sealed class OtlpResource
{
    [JsonPropertyName("attributes")]
    public List<OtlpKeyValue> Attributes { get; set; } = new();
}

public sealed class OtlpKeyValue
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public OtlpAnyValue? Value { get; set; }
}

/// <summary>
/// OTLP <c>AnyValue</c>. Only one of the typed fields is set per
/// instance per the OTLP spec; consumers read whichever matches.
/// </summary>
public sealed class OtlpAnyValue
{
    [JsonPropertyName("stringValue")]
    public string? StringValue { get; set; }

    [JsonPropertyName("boolValue")]
    public bool? BoolValue { get; set; }

    [JsonPropertyName("intValue")]
    public string? IntValue { get; set; }

    [JsonPropertyName("doubleValue")]
    public double? DoubleValue { get; set; }

    [JsonPropertyName("arrayValue")]
    public OtlpArrayValue? ArrayValue { get; set; }

    [JsonPropertyName("kvlistValue")]
    public OtlpKeyValueList? KvlistValue { get; set; }
}

public sealed class OtlpArrayValue
{
    [JsonPropertyName("values")]
    public List<OtlpAnyValue> Values { get; set; } = new();
}

public sealed class OtlpKeyValueList
{
    [JsonPropertyName("values")]
    public List<OtlpKeyValue> Values { get; set; } = new();
}

/// <summary>
/// Empty success response shape — OTLP returns <c>{"partialSuccess": {}}</c>
/// for a fully-accepted batch.
/// </summary>
public sealed class OtlpAcceptedResponse
{
    [JsonPropertyName("partialSuccess")]
    public JsonElement PartialSuccess { get; set; } = JsonSerializer.SerializeToElement(new { });
}
