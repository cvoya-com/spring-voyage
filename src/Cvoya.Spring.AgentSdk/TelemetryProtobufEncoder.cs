// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Globalization;

using Google.Protobuf;

/// <summary>
/// Minimal OTLP/HTTP+protobuf encoder for the .NET SDK's emit path
/// (issue #2501).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the wire shape the platform ingest decoder
/// (<c>Cvoya.Spring.Host.Api.Endpoints.Otlp.OtlpProtobufDecoder</c>)
/// reads. The two sides intentionally use the same Google.Protobuf
/// primitives but do not share a generated message closure — the OSS
/// surface has no public <c>OpenTelemetry.Proto</c> NuGet to depend on.
/// </para>
/// <para>
/// The encoder accepts the same dictionary-shaped span the JSON
/// pathway uses internally, so the call-site in
/// <see cref="TelemetryClient"/> stays unchanged between the protobuf
/// and JSON branches.
/// </para>
/// </remarks>
internal static class TelemetryProtobufEncoder
{
    /// <summary>
    /// Encodes one outbound <c>ExportTraceServiceRequest</c> envelope
    /// carrying a single span. The SDK posts one span per request — the
    /// telemetry path is not batched.
    /// </summary>
    /// <param name="span">Span shape produced by <see cref="TelemetryClient"/>.</param>
    /// <param name="resourceAttributes">Resource attributes stamped on every payload.</param>
    /// <param name="scopeName">Instrumentation scope name.</param>
    /// <param name="scopeVersion">Instrumentation scope version.</param>
    public static byte[] EncodeTraceEnvelope(
        IDictionary<string, object?> span,
        IReadOnlyDictionary<string, string> resourceAttributes,
        string scopeName,
        string scopeVersion)
    {
        return WriteRoot(output =>
        {
            WriteSubMessage(output, 1, resourceSpans =>
            {
                WriteSubMessage(resourceSpans, 1, resource =>
                {
                    foreach (var attr in resourceAttributes)
                    {
                        WriteSubMessage(resource, 1, kv => WriteStringKv(kv, attr.Key, attr.Value));
                    }
                });
                WriteSubMessage(resourceSpans, 2, scopeSpans =>
                {
                    // Field 1 is "InstrumentationScope scope". The
                    // ingest mapper doesn't read scope fields, so we
                    // omit them entirely.
                    _ = scopeName;
                    _ = scopeVersion;

                    WriteSubMessage(scopeSpans, 2, spanOut => WriteSpan(spanOut, span));
                });
            });
        });
    }

    private static void WriteSpan(CodedOutputStream output, IDictionary<string, object?> span)
    {
        if (span.TryGetValue("traceId", out var t) && t is string traceHex && !string.IsNullOrEmpty(traceHex))
        {
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(traceHex));
        }
        if (span.TryGetValue("spanId", out var s) && s is string spanHex && !string.IsNullOrEmpty(spanHex))
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(spanHex));
        }
        if (span.TryGetValue("parentSpanId", out var p) && p is string parentHex && !string.IsNullOrEmpty(parentHex))
        {
            output.WriteTag(4, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(HexToBytes(parentHex));
        }
        if (span.TryGetValue("name", out var n) && n is string name)
        {
            output.WriteTag(5, WireFormat.WireType.LengthDelimited);
            output.WriteString(name);
        }
        if (span.TryGetValue("kind", out var k) && k is int kindInt)
        {
            output.WriteTag(6, WireFormat.WireType.Varint);
            output.WriteInt32(kindInt);
        }
        if (span.TryGetValue("startTimeUnixNano", out var st)
            && st is string startStr
            && long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startNanos))
        {
            output.WriteTag(7, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)startNanos);
        }
        if (span.TryGetValue("endTimeUnixNano", out var et)
            && et is string endStr
            && long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endNanos))
        {
            output.WriteTag(8, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)endNanos);
        }
        if (span.TryGetValue("attributes", out var attrs) && attrs is IEnumerable<object> attrList)
        {
            foreach (var entry in attrList)
            {
                WriteSubMessage(output, 9, kv => WriteJsonKv(kv, entry));
            }
        }
        if (span.TryGetValue("events", out var events) && events is IEnumerable<object> eventList)
        {
            foreach (var entry in eventList)
            {
                WriteSubMessage(output, 11, ev => WriteSpanEvent(ev, entry));
            }
        }
        if (span.TryGetValue("status", out var statusObj) && statusObj is IDictionary<string, object?> statusDict)
        {
            WriteSubMessage(output, 15, st2 =>
            {
                if (statusDict.TryGetValue("message", out var m) && m is string msg)
                {
                    st2.WriteTag(2, WireFormat.WireType.LengthDelimited);
                    st2.WriteString(msg);
                }
                if (statusDict.TryGetValue("code", out var c) && c is int code)
                {
                    st2.WriteTag(3, WireFormat.WireType.Varint);
                    st2.WriteInt32(code);
                }
            });
        }
    }

    private static void WriteSpanEvent(CodedOutputStream output, object entry)
    {
        // TelemetryClient builds span events via reflection-friendly
        // anonymous types but the dictionary-shaped form is what we
        // accept here. The current call-site emits an anonymous-typed
        // record; we read it by reflection for the few fields we need.
        var type = entry.GetType();
        var nameProp = type.GetProperty("name");
        var timeProp = type.GetProperty("timeUnixNano");
        var attrsProp = type.GetProperty("attributes");

        if (timeProp?.GetValue(entry) is string timeStr
            && long.TryParse(timeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            output.WriteTag(1, WireFormat.WireType.Fixed64);
            output.WriteFixed64((ulong)nanos);
        }
        if (nameProp?.GetValue(entry) is string evName)
        {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(evName);
        }
        if (attrsProp?.GetValue(entry) is IEnumerable<object> evAttrs)
        {
            foreach (var attr in evAttrs)
            {
                WriteSubMessage(output, 3, kv => WriteJsonKv(kv, attr));
            }
        }
    }

    private static void WriteJsonKv(CodedOutputStream output, object entry)
    {
        // TelemetryClient stamps anonymous types of the form `{ key, value }`
        // onto the attribute list. Read them via reflection so this encoder
        // stays compatible with the existing builder shape.
        var type = entry.GetType();
        var keyProp = type.GetProperty("key");
        var valueProp = type.GetProperty("value");

        var key = keyProp?.GetValue(entry) as string ?? string.Empty;
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(key);

        var value = valueProp?.GetValue(entry);
        if (value is null)
        {
            return;
        }
        WriteSubMessage(output, 2, any => WriteAnyValue(any, value));
    }

    private static void WriteStringKv(CodedOutputStream output, string key, string value)
    {
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(key);
        WriteSubMessage(output, 2, any =>
        {
            any.WriteTag(1, WireFormat.WireType.LengthDelimited);
            any.WriteString(value);
        });
    }

    private static void WriteAnyValue(CodedOutputStream output, object value)
    {
        // The TelemetryClient builder emits a dictionary like
        // { stringValue = "..." } / { intValue = "123" } / etc.
        if (value is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("stringValue", out var sv) && sv is string s)
            {
                output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                output.WriteString(s);
                return;
            }
            if (dict.TryGetValue("boolValue", out var bv) && bv is bool b)
            {
                output.WriteTag(2, WireFormat.WireType.Varint);
                output.WriteBool(b);
                return;
            }
            if (dict.TryGetValue("intValue", out var iv) && iv is string istr
                && long.TryParse(istr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                output.WriteTag(3, WireFormat.WireType.Varint);
                output.WriteInt64(i);
                return;
            }
            if (dict.TryGetValue("doubleValue", out var dv) && dv is double d)
            {
                output.WriteTag(4, WireFormat.WireType.Fixed64);
                output.WriteDouble(d);
                return;
            }
        }

        // Fallback: coerce to string. Preserves the value at the cost of
        // an over-broad type tag.
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString(value?.ToString() ?? string.Empty);
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
