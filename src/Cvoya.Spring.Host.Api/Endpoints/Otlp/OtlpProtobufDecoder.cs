// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints.Otlp;

using System.Globalization;

using Google.Protobuf;

/// <summary>
/// Minimal OTLP/HTTP+protobuf wire decoder for issue #2501.
/// </summary>
/// <remarks>
/// <para>
/// The OTLP collector defines its export-service request shapes in
/// <c>opentelemetry/proto/collector/{trace,logs}/v1/{trace,logs}_service.proto</c>.
/// Rather than ship the entire generated C# closure (no public
/// <c>OpenTelemetry.Proto</c> NuGet exists; the OpenTelemetry .NET
/// exporter keeps its proto types <c>internal</c>), we use
/// <see cref="Google.Protobuf.CodedInputStream"/> — Google's official
/// wire parser — to read only the fields the ingest pipeline actually
/// consumes, then project them into the existing JSON-pathway POCOs
/// (<see cref="OtlpTracesRequest"/> / <see cref="OtlpLogsRequest"/>).
/// </para>
/// <para>
/// Unknown fields are skipped by <c>CodedInputStream.SkipLastField</c>,
/// so a producer that sets richer fields (status, dropped counts,
/// instrumentation scope versions) will not be rejected — they are
/// simply ignored, matching the JSON pathway's tolerance. Field numbers
/// below match the upstream <c>.proto</c> definitions at
/// <c>opentelemetry-proto v1.7.0</c> (the wire numbers are stable across
/// minor versions and have been since OTLP 0.18).
/// </para>
/// <para>
/// Length-delimited submessages are read into a byte slice and recursed
/// into a fresh <see cref="CodedInputStream"/> — the public Google.Protobuf
/// surface no longer exposes the older <c>PushLimit</c>/<c>PopLimit</c>
/// pair, but a fresh per-message parser is just as correct and avoids
/// reflection over private APIs.
/// </para>
/// </remarks>
public static class OtlpProtobufDecoder
{
    /// <summary>
    /// MIME type the OTLP/HTTP spec defines for protobuf payloads.
    /// </summary>
    public const string ContentType = "application/x-protobuf";

    /// <summary>
    /// Decodes an OTLP <c>ExportTraceServiceRequest</c> protobuf payload
    /// into the JSON-pathway <see cref="OtlpTracesRequest"/> shape.
    /// </summary>
    public static OtlpTracesRequest DecodeTraces(ReadOnlySpan<byte> payload)
    {
        var request = new OtlpTracesRequest();
        if (payload.IsEmpty)
        {
            return request;
        }
        var input = new CodedInputStream(payload.ToArray());
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // repeated ResourceSpans resource_spans = 1;
                    request.ResourceSpans.Add(DecodeSubMessage(input, ReadResourceSpans));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return request;
    }

    /// <summary>
    /// Decodes an OTLP <c>ExportLogsServiceRequest</c> protobuf payload
    /// into the JSON-pathway <see cref="OtlpLogsRequest"/> shape.
    /// </summary>
    public static OtlpLogsRequest DecodeLogs(ReadOnlySpan<byte> payload)
    {
        var request = new OtlpLogsRequest();
        if (payload.IsEmpty)
        {
            return request;
        }
        var input = new CodedInputStream(payload.ToArray());
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // repeated ResourceLogs resource_logs = 1;
                    request.ResourceLogs.Add(DecodeSubMessage(input, ReadResourceLogs));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return request;
    }

    private static T DecodeSubMessage<T>(CodedInputStream input, Func<CodedInputStream, T> reader)
    {
        // A submessage on the OTLP wire is length-delimited (wire type 2).
        // ReadBytes consumes the length prefix and returns the payload as
        // a ByteString — exactly what a submessage header carries. We then
        // recurse into a fresh CodedInputStream over those bytes.
        var bytes = input.ReadBytes();
        var inner = new CodedInputStream(bytes.ToByteArray());
        return reader(inner);
    }

    // -------------------------------------------------------------------------
    // Traces
    // -------------------------------------------------------------------------

    private static OtlpResourceSpans ReadResourceSpans(CodedInputStream input)
    {
        var result = new OtlpResourceSpans();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // Resource resource = 1;
                    result.Resource = DecodeSubMessage(input, ReadResource);
                    break;
                case 2: // repeated ScopeSpans scope_spans = 2;
                    result.ScopeSpans.Add(DecodeSubMessage(input, ReadScopeSpans));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return result;
    }

    private static OtlpScopeSpans ReadScopeSpans(CodedInputStream input)
    {
        var result = new OtlpScopeSpans();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: // repeated Span spans = 2;
                    result.Spans.Add(DecodeSubMessage(input, ReadSpan));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return result;
    }

    private static OtlpSpan ReadSpan(CodedInputStream input)
    {
        var span = new OtlpSpan();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // bytes trace_id = 1;
                    span.TraceId = BytesToHex(input.ReadBytes());
                    break;
                case 2: // bytes span_id = 2;
                    span.SpanId = BytesToHex(input.ReadBytes());
                    break;
                case 4: // bytes parent_span_id = 4;
                    span.ParentSpanId = BytesToHex(input.ReadBytes());
                    break;
                case 5: // string name = 5;
                    span.Name = input.ReadString();
                    break;
                case 6: // SpanKind kind = 6 (enum, varint);
                    span.Kind = input.ReadInt32();
                    break;
                case 7: // fixed64 start_time_unix_nano = 7;
                    span.StartTimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 8: // fixed64 end_time_unix_nano = 8;
                    span.EndTimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 9: // repeated KeyValue attributes = 9;
                    span.Attributes.Add(DecodeSubMessage(input, ReadKeyValue));
                    break;
                case 11: // repeated Event events = 11;
                    span.Events.Add(DecodeSubMessage(input, ReadSpanEvent));
                    break;
                case 15: // Status status = 15;
                    span.Status = DecodeSubMessage(input, ReadStatus);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return span;
    }

    private static OtlpSpanEvent ReadSpanEvent(CodedInputStream input)
    {
        var ev = new OtlpSpanEvent();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // fixed64 time_unix_nano = 1;
                    ev.TimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 2: // string name = 2;
                    ev.Name = input.ReadString();
                    break;
                case 3: // repeated KeyValue attributes = 3;
                    ev.Attributes.Add(DecodeSubMessage(input, ReadKeyValue));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return ev;
    }

    private static OtlpSpanStatus ReadStatus(CodedInputStream input)
    {
        var status = new OtlpSpanStatus();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: // string message = 2;
                    status.Message = input.ReadString();
                    break;
                case 3: // StatusCode code = 3;
                    status.Code = input.ReadInt32();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return status;
    }

    // -------------------------------------------------------------------------
    // Logs
    // -------------------------------------------------------------------------

    private static OtlpResourceLogs ReadResourceLogs(CodedInputStream input)
    {
        var result = new OtlpResourceLogs();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // Resource resource = 1;
                    result.Resource = DecodeSubMessage(input, ReadResource);
                    break;
                case 2: // repeated ScopeLogs scope_logs = 2;
                    result.ScopeLogs.Add(DecodeSubMessage(input, ReadScopeLogs));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return result;
    }

    private static OtlpScopeLogs ReadScopeLogs(CodedInputStream input)
    {
        var result = new OtlpScopeLogs();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 2: // repeated LogRecord log_records = 2;
                    result.LogRecords.Add(DecodeSubMessage(input, ReadLogRecord));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return result;
    }

    private static OtlpLogRecord ReadLogRecord(CodedInputStream input)
    {
        var record = new OtlpLogRecord();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // fixed64 time_unix_nano = 1;
                    record.TimeUnixNano = input.ReadFixed64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 2: // SeverityNumber severity_number = 2;
                    record.SeverityNumber = input.ReadInt32();
                    break;
                case 3: // string severity_text = 3;
                    record.SeverityText = input.ReadString();
                    break;
                case 5: // AnyValue body = 5;
                    record.Body = DecodeSubMessage(input, ReadAnyValue);
                    break;
                case 6: // repeated KeyValue attributes = 6;
                    record.Attributes.Add(DecodeSubMessage(input, ReadKeyValue));
                    break;
                case 9: // bytes trace_id = 9;
                    record.TraceId = BytesToHex(input.ReadBytes());
                    break;
                case 10: // bytes span_id = 10;
                    record.SpanId = BytesToHex(input.ReadBytes());
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return record;
    }

    // -------------------------------------------------------------------------
    // Shared message types (Resource, KeyValue, AnyValue)
    // -------------------------------------------------------------------------

    private static OtlpResource ReadResource(CodedInputStream input)
    {
        var resource = new OtlpResource();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // repeated KeyValue attributes = 1;
                    resource.Attributes.Add(DecodeSubMessage(input, ReadKeyValue));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return resource;
    }

    private static OtlpKeyValue ReadKeyValue(CodedInputStream input)
    {
        var kv = new OtlpKeyValue();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // string key = 1;
                    kv.Key = input.ReadString();
                    break;
                case 2: // AnyValue value = 2;
                    kv.Value = DecodeSubMessage(input, ReadAnyValue);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return kv;
    }

    private static OtlpAnyValue ReadAnyValue(CodedInputStream input)
    {
        var any = new OtlpAnyValue();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // string string_value = 1;
                    any.StringValue = input.ReadString();
                    break;
                case 2: // bool bool_value = 2;
                    any.BoolValue = input.ReadBool();
                    break;
                case 3: // int64 int_value = 3;
                    any.IntValue = input.ReadInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                case 4: // double double_value = 4;
                    any.DoubleValue = input.ReadDouble();
                    break;
                case 5: // ArrayValue array_value = 5;
                    any.ArrayValue = DecodeSubMessage(input, ReadArrayValue);
                    break;
                case 6: // KeyValueList kvlist_value = 6;
                    any.KvlistValue = DecodeSubMessage(input, ReadKeyValueList);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return any;
    }

    private static OtlpArrayValue ReadArrayValue(CodedInputStream input)
    {
        var arr = new OtlpArrayValue();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // repeated AnyValue values = 1;
                    arr.Values.Add(DecodeSubMessage(input, ReadAnyValue));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return arr;
    }

    private static OtlpKeyValueList ReadKeyValueList(CodedInputStream input)
    {
        var list = new OtlpKeyValueList();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // repeated KeyValue values = 1;
                    list.Values.Add(DecodeSubMessage(input, ReadKeyValue));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return list;
    }

    private static string BytesToHex(ByteString bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }
        // OTLP traces use raw 16-byte trace ids and 8-byte span ids; the
        // JSON-pathway POCOs carry them as lowercase hex strings, so we
        // normalise here.
        return Convert.ToHexString(bytes.Span).ToLowerInvariant();
    }
}

/// <summary>
/// Encoder of the minimal protobuf payloads our tests need (we don't
/// hand a producer-side codec to runtime consumers — they already have
/// official OTel SDKs). Lives in this file so the decoder + encoder
/// stay co-located when the wire-format evolves.
/// </summary>
public static class OtlpProtobufTestEncoder
{
    /// <summary>
    /// Encodes an <see cref="OtlpTracesRequest"/> to its OTLP protobuf
    /// representation. Used by ingest-side tests to produce a known
    /// payload that the decoder must round-trip back into an equivalent
    /// JSON-pathway request.
    /// </summary>
    public static byte[] EncodeTraces(OtlpTracesRequest request)
    {
        return WriteRoot(output =>
        {
            foreach (var resourceSpans in request.ResourceSpans)
            {
                WriteSubMessage(output, 1, inner => WriteResourceSpans(inner, resourceSpans));
            }
        });
    }

    /// <summary>
    /// Encodes an <see cref="OtlpLogsRequest"/> to its OTLP protobuf
    /// representation.
    /// </summary>
    public static byte[] EncodeLogs(OtlpLogsRequest request)
    {
        return WriteRoot(output =>
        {
            foreach (var resourceLogs in request.ResourceLogs)
            {
                WriteSubMessage(output, 1, inner => WriteResourceLogs(inner, resourceLogs));
            }
        });
    }

    private static void WriteResourceSpans(CodedOutputStream output, OtlpResourceSpans resourceSpans)
    {
        if (resourceSpans.Resource is not null)
        {
            WriteSubMessage(output, 1, inner => WriteResource(inner, resourceSpans.Resource));
        }
        foreach (var scope in resourceSpans.ScopeSpans)
        {
            WriteSubMessage(output, 2, inner => WriteScopeSpans(inner, scope));
        }
    }

    private static void WriteScopeSpans(CodedOutputStream output, OtlpScopeSpans scopeSpans)
    {
        foreach (var span in scopeSpans.Spans)
        {
            WriteSubMessage(output, 2, inner => WriteSpan(inner, span));
        }
    }

    private static void WriteSpan(CodedOutputStream output, OtlpSpan span)
    {
        if (!string.IsNullOrEmpty(span.TraceId))
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(span.TraceId));
        }
        if (!string.IsNullOrEmpty(span.SpanId))
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(span.SpanId));
        }
        if (!string.IsNullOrEmpty(span.ParentSpanId))
        {
            output.WriteTag(4, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(span.ParentSpanId));
        }
        if (span.Name is not null)
        {
            output.WriteTag(5, WireFormat.WireType.LengthDelimited);
            output.WriteString(span.Name);
        }
        if (span.Kind is not null)
        {
            output.WriteTag(6, WireFormat.WireType.Varint);
            output.WriteInt32(span.Kind.Value);
        }
        if (long.TryParse(span.StartTimeUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startNanos))
        {
            output.WriteTag(7, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)startNanos);
        }
        if (long.TryParse(span.EndTimeUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endNanos))
        {
            output.WriteTag(8, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)endNanos);
        }
        foreach (var kv in span.Attributes)
        {
            WriteSubMessage(output, 9, inner => WriteKeyValue(inner, kv));
        }
        foreach (var ev in span.Events)
        {
            WriteSubMessage(output, 11, inner => WriteSpanEvent(inner, ev));
        }
        if (span.Status is not null)
        {
            WriteSubMessage(output, 15, inner => WriteSpanStatus(inner, span.Status));
        }
    }

    private static void WriteSpanEvent(CodedOutputStream output, OtlpSpanEvent ev)
    {
        if (long.TryParse(ev.TimeUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            output.WriteTag(1, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)nanos);
        }
        if (ev.Name is not null)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(ev.Name);
        }
        foreach (var kv in ev.Attributes)
        {
            WriteSubMessage(output, 3, inner => WriteKeyValue(inner, kv));
        }
    }

    private static void WriteSpanStatus(CodedOutputStream output, OtlpSpanStatus status)
    {
        if (status.Message is not null)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(status.Message);
        }
        if (status.Code is not null)
        {
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteInt32(status.Code.Value);
        }
    }

    private static void WriteResourceLogs(CodedOutputStream output, OtlpResourceLogs resourceLogs)
    {
        if (resourceLogs.Resource is not null)
        {
            WriteSubMessage(output, 1, inner => WriteResource(inner, resourceLogs.Resource));
        }
        foreach (var scope in resourceLogs.ScopeLogs)
        {
            WriteSubMessage(output, 2, inner => WriteScopeLogs(inner, scope));
        }
    }

    private static void WriteScopeLogs(CodedOutputStream output, OtlpScopeLogs scopeLogs)
    {
        foreach (var record in scopeLogs.LogRecords)
        {
            WriteSubMessage(output, 2, inner => WriteLogRecord(inner, record));
        }
    }

    private static void WriteLogRecord(CodedOutputStream output, OtlpLogRecord record)
    {
        if (long.TryParse(record.TimeUnixNano, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            output.WriteTag(1, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)nanos);
        }
        if (record.SeverityNumber is not null)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt32(record.SeverityNumber.Value);
        }
        if (record.SeverityText is not null)
        {
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteString(record.SeverityText);
        }
        if (record.Body is not null)
        {
            WriteSubMessage(output, 5, inner => WriteAnyValue(inner, record.Body));
        }
        foreach (var kv in record.Attributes)
        {
            WriteSubMessage(output, 6, inner => WriteKeyValue(inner, kv));
        }
    }

    private static void WriteResource(CodedOutputStream output, OtlpResource resource)
    {
        foreach (var kv in resource.Attributes)
        {
            WriteSubMessage(output, 1, inner => WriteKeyValue(inner, kv));
        }
    }

    private static void WriteKeyValue(CodedOutputStream output, OtlpKeyValue kv)
    {
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(kv.Key);
        if (kv.Value is not null)
        {
            WriteSubMessage(output, 2, inner => WriteAnyValue(inner, kv.Value));
        }
    }

    private static void WriteAnyValue(CodedOutputStream output, OtlpAnyValue value)
    {
        if (value.StringValue is not null)
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteString(value.StringValue);
            return;
        }
        if (value.BoolValue is not null)
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteBool(value.BoolValue.Value);
            return;
        }
        if (value.IntValue is not null
            && long.TryParse(value.IntValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
        {
            output.WriteTag(3, WireFormat.WireType.Varint);
            output.WriteInt64(iv);
            return;
        }
        if (value.DoubleValue is not null)
        {
            output.WriteTag(4, WireFormat.WireType.Fixed64);
            output.WriteDouble(value.DoubleValue.Value);
        }
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
        // Submessages on the OTLP wire are length-delimited (wire type 2).
        // CodedOutputStream's public surface doesn't expose WriteRawBytes,
        // but WriteBytes(ByteString) emits length + bytes — exactly the
        // submessage tail shape. So we materialise the submessage to a
        // ByteString once and let WriteBytes write the header for us.
        var bytes = WriteRoot(write);
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }

    private static ByteString HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return ByteString.Empty;
        }
        var len = hex.Length / 2;
        var bytes = new byte[len];
        for (var i = 0; i < len; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return ByteString.CopyFrom(bytes);
    }
}
