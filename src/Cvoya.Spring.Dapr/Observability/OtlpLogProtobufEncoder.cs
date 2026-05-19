// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Capabilities;

using Google.Protobuf;

/// <summary>
/// Minimal OTLP/HTTP+protobuf encoder for the activity forwarder's
/// <c>http/protobuf</c> tenants (issue #2511).
/// </summary>
/// <remarks>
/// <para>
/// Emits an <c>ExportLogsServiceRequest</c> envelope that mirrors the
/// JSON shape produced by
/// <see cref="ForwardingOtlpIngestServiceDecorator"/> field-for-field, so
/// the protobuf path is observable-equivalent to the JSON path for any
/// collector that accepts both. The minimal OTLP message shapes are
/// inlined via <see cref="CodedOutputStream"/> for the same reason the
/// ingest decoder in <c>Cvoya.Spring.Host.Api</c> inlines them: no public
/// <c>OpenTelemetry.Proto</c> NuGet exists and the OpenTelemetry .NET
/// exporter keeps its generated types <c>internal</c>.
/// </para>
/// <para>
/// Field numbers below match the upstream <c>.proto</c> definitions at
/// <c>opentelemetry-proto v1.7.0</c>; the wire numbers are stable across
/// minor versions and have been since OTLP 0.18.
/// </para>
/// </remarks>
internal static class OtlpLogProtobufEncoder
{
    /// <summary>OTLP/HTTP MIME type for protobuf payloads.</summary>
    public const string ContentType = "application/x-protobuf";

    /// <summary>Resource attribute key for the service name.</summary>
    private const string ServiceNameKey = "service.name";

    /// <summary>Resource attribute value for the service name.</summary>
    private const string ServiceNameValue = "spring-voyage";

    /// <summary>Resource attribute key for the platform tenant id.</summary>
    private const string TenantIdKey = "sv.tenant.id";

    /// <summary>Instrumentation scope name stamped on every forwarded batch.</summary>
    private const string ScopeName = "spring-voyage.activity.forward";

    /// <summary>Instrumentation scope version stamped on every forwarded batch.</summary>
    private const string ScopeVersion = "0.1.0";

    /// <summary>
    /// Encodes the given activity events as an OTLP
    /// <c>ExportLogsServiceRequest</c> protobuf payload. Each event
    /// projects to one <c>LogRecord</c> carrying the platform's activity
    /// summary, severity, and already-redacted details payload.
    /// </summary>
    /// <param name="events">
    /// Events to encode. Must be non-empty; the first event's
    /// <see cref="OtlpEventIngest.TenantId"/> is stamped onto the
    /// resource attributes (the forwarder batches per-tenant).
    /// </param>
    public static byte[] EncodeLogs(IReadOnlyList<OtlpEventIngest> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return Array.Empty<byte>();
        }

        return WriteRoot(output =>
        {
            // ExportLogsServiceRequest.resource_logs = 1; (repeated).
            // The forwarder batches per tenant so we emit a single
            // ResourceLogs carrying every event.
            WriteSubMessage(output, 1, resourceLogs => WriteResourceLogs(resourceLogs, events));
        });
    }

    private static void WriteResourceLogs(CodedOutputStream output, IReadOnlyList<OtlpEventIngest> events)
    {
        // ResourceLogs.resource = 1;
        WriteSubMessage(output, 1, resource =>
        {
            // Resource.attributes = 1; (repeated KeyValue).
            WriteSubMessage(resource, 1, kv => WriteStringKv(kv, ServiceNameKey, ServiceNameValue));
            WriteSubMessage(resource, 1, kv => WriteStringKv(kv, TenantIdKey, events[0].TenantId.ToString("D")));
        });

        // ResourceLogs.scope_logs = 2; (repeated).
        WriteSubMessage(output, 2, scopeLogs => WriteScopeLogs(scopeLogs, events));
    }

    private static void WriteScopeLogs(CodedOutputStream output, IReadOnlyList<OtlpEventIngest> events)
    {
        // ScopeLogs.scope = 1; (InstrumentationScope).
        WriteSubMessage(output, 1, scope =>
        {
            // InstrumentationScope.name = 1; .version = 2;
            scope.WriteTag(1, WireFormat.WireType.LengthDelimited);
            scope.WriteString(ScopeName);
            scope.WriteTag(2, WireFormat.WireType.LengthDelimited);
            scope.WriteString(ScopeVersion);
        });

        // ScopeLogs.log_records = 2; (repeated).
        foreach (var evt in events)
        {
            WriteSubMessage(output, 2, logRecord => WriteLogRecord(logRecord, evt));
        }
    }

    private static void WriteLogRecord(CodedOutputStream output, OtlpEventIngest evt)
    {
        // LogRecord.time_unix_nano = 1; (fixed64). Mirror the JSON
        // path's millisecond-resolution clamp so the two wire shapes
        // stay observable-equivalent for any collector that ingests
        // both protocols off the same tenant.
        var unixNanos = (ulong)((long)(evt.Timestamp - DateTimeOffset.UnixEpoch).TotalMilliseconds * 1_000_000L);
        output.WriteTag(1, WireFormat.WireType.Fixed64);
        output.WriteFixed64(unixNanos);

        // LogRecord.severity_text = 3; (string).
        output.WriteTag(3, WireFormat.WireType.LengthDelimited);
        output.WriteString(evt.Severity.ToString().ToUpperInvariant());

        // LogRecord.body = 5; (AnyValue) — string_value carrying the
        // platform-shaped summary, mirroring the JSON envelope.
        WriteSubMessage(output, 5, body =>
        {
            body.WriteTag(1, WireFormat.WireType.LengthDelimited);
            body.WriteString(evt.Summary);
        });

        // LogRecord.attributes = 6; (repeated KeyValue) — mirror the
        // JSON path's attribute set field-for-field so collectors that
        // already ingest the JSON shape see the same keys / values.
        WriteSubMessage(output, 6, kv => WriteStringKv(kv, "sv.event.kind", evt.Kind.ToString()));
        WriteSubMessage(output, 6, kv => WriteStringKv(kv, "sv.subject", evt.Subject.ToString()));
        WriteSubMessage(output, 6, kv => WriteStringKv(kv, "sv.thread.id", evt.ThreadId ?? string.Empty));
        WriteSubMessage(output, 6, kv => WriteStringKv(kv, "sv.message.id", evt.MessageId ?? string.Empty));
        WriteSubMessage(output, 6, kv => WriteStringKv(kv, "sv.details", evt.Details.GetRawText()));

        // LogRecord.trace_id = 9; (bytes) and .span_id = 10; (bytes) are
        // intentionally omitted: the activity bus normalises spans/logs
        // through Map{Traces,Logs} before they reach this forwarder and
        // the original trace/span ids are not carried on
        // OtlpEventIngest. An external collector seeing a LogRecord
        // without trace_id / span_id treats it as un-correlated, which
        // is the truthful representation for the platform-shaped
        // activity stream.
    }

    private static void WriteStringKv(CodedOutputStream output, string key, string value)
    {
        // KeyValue.key = 1; (string).
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(key);
        // KeyValue.value = 2; (AnyValue).
        WriteSubMessage(output, 2, any =>
        {
            // AnyValue.string_value = 1;
            any.WriteTag(1, WireFormat.WireType.LengthDelimited);
            any.WriteString(value);
        });
    }

    private static byte[] WriteRoot(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            write(output);
            output.Flush();
        }
        return stream.ToArray();
    }

    private static void WriteSubMessage(CodedOutputStream output, int fieldNumber, Action<CodedOutputStream> write)
    {
        // Submessages on the OTLP wire are length-delimited (wire type
        // 2). CodedOutputStream's public surface doesn't expose
        // WriteRawBytes, but WriteBytes(ByteString) emits length + bytes
        // — exactly the submessage tail shape. So we materialise the
        // submessage to a ByteString once and let WriteBytes write the
        // header for us.
        var bytes = WriteRoot(write);
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }
}
