// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Otlp;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Host.Api.Endpoints.Otlp;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2501: round-trips an OTLP/HTTP+protobuf payload through
/// <see cref="OtlpProtobufTestEncoder"/> + <see cref="OtlpProtobufDecoder"/>
/// and asserts that the decoded request matches what the JSON pathway
/// would have produced. The decoder hands its output to the existing
/// <see cref="OtlpEventMapper"/>, so by reusing the JSON-pathway POCOs
/// we transitively re-test the mapper too.
/// </summary>
public class OtlpProtobufDecoderTests
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SubjectA = new("11111111-0000-0000-0000-000000000001");

    [Fact]
    public void DecodeTraces_RoundTripsKnownPayload()
    {
        var original = new OtlpTracesRequest
        {
            ResourceSpans =
            {
                new OtlpResourceSpans
                {
                    Resource = ResourceFor(TenantA, SubjectA, "agent"),
                    ScopeSpans =
                    {
                        new OtlpScopeSpans
                        {
                            Spans =
                            {
                                new OtlpSpan
                                {
                                    Name = "sv.agent.invoke",
                                    TraceId = "0102030405060708090a0b0c0d0e0f10",
                                    SpanId = "1112131415161718",
                                    StartTimeUnixNano = "1700000000000000000",
                                    EndTimeUnixNano = "1700000000999000000",
                                    Attributes =
                                    {
                                        new OtlpKeyValue
                                        {
                                            Key = "k1",
                                            Value = new OtlpAnyValue { StringValue = "v1" },
                                        },
                                    },
                                    Events =
                                    {
                                        new OtlpSpanEvent
                                        {
                                            Name = "sv.progress",
                                            TimeUnixNano = "1700000000500000000",
                                            Attributes =
                                            {
                                                new OtlpKeyValue
                                                {
                                                    Key = "message",
                                                    Value = new OtlpAnyValue { StringValue = "halfway" },
                                                },
                                            },
                                        },
                                    },
                                    Status = new OtlpSpanStatus { Code = 0, Message = "ok" },
                                },
                            },
                        },
                    },
                },
            },
        };

        var bytes = OtlpProtobufTestEncoder.EncodeTraces(original);
        bytes.Length.ShouldBeGreaterThan(0);

        var decoded = OtlpProtobufDecoder.DecodeTraces(bytes);

        decoded.ResourceSpans.Count.ShouldBe(1);
        var rs = decoded.ResourceSpans[0];
        rs.Resource.ShouldNotBeNull();
        rs.ScopeSpans.Count.ShouldBe(1);
        var span = rs.ScopeSpans[0].Spans[0];
        span.Name.ShouldBe("sv.agent.invoke");
        span.TraceId.ShouldBe("0102030405060708090a0b0c0d0e0f10");
        span.SpanId.ShouldBe("1112131415161718");
        span.StartTimeUnixNano.ShouldBe("1700000000000000000");
        span.EndTimeUnixNano.ShouldBe("1700000000999000000");
        span.Attributes.ShouldContain(a => a.Key == "k1" && a.Value!.StringValue == "v1");
        span.Events.Count.ShouldBe(1);
        span.Events[0].Name.ShouldBe("sv.progress");
        span.Events[0].Attributes.ShouldContain(a => a.Key == "message" && a.Value!.StringValue == "halfway");
        span.Status.ShouldNotBeNull();
        span.Status!.Message.ShouldBe("ok");
    }

    [Fact]
    public void DecodeLogs_RoundTripsKnownPayload()
    {
        var original = new OtlpLogsRequest
        {
            ResourceLogs =
            {
                new OtlpResourceLogs
                {
                    Resource = ResourceFor(TenantA, SubjectA, "agent"),
                    ScopeLogs =
                    {
                        new OtlpScopeLogs
                        {
                            LogRecords =
                            {
                                new OtlpLogRecord
                                {
                                    TimeUnixNano = "1700000000000000000",
                                    SeverityNumber = 9,
                                    SeverityText = "INFO",
                                    Body = new OtlpAnyValue { StringValue = "hello world" },
                                    Attributes =
                                    {
                                        new OtlpKeyValue
                                        {
                                            Key = "request.id",
                                            Value = new OtlpAnyValue { StringValue = "abc" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var bytes = OtlpProtobufTestEncoder.EncodeLogs(original);
        bytes.Length.ShouldBeGreaterThan(0);

        var decoded = OtlpProtobufDecoder.DecodeLogs(bytes);

        decoded.ResourceLogs.Count.ShouldBe(1);
        var rl = decoded.ResourceLogs[0];
        rl.Resource.ShouldNotBeNull();
        rl.ScopeLogs.Count.ShouldBe(1);
        var record = rl.ScopeLogs[0].LogRecords[0];
        record.TimeUnixNano.ShouldBe("1700000000000000000");
        record.SeverityNumber.ShouldBe(9);
        record.SeverityText.ShouldBe("INFO");
        record.Body!.StringValue.ShouldBe("hello world");
        record.Attributes.ShouldContain(a => a.Key == "request.id" && a.Value!.StringValue == "abc");
    }

    [Fact]
    public void DecodeTraces_EmptyPayload_YieldsEmptyRequest()
    {
        var decoded = OtlpProtobufDecoder.DecodeTraces(Array.Empty<byte>());
        decoded.ResourceSpans.ShouldBeEmpty();
    }

    [Fact]
    public void DecodeLogs_EmptyPayload_YieldsEmptyRequest()
    {
        var decoded = OtlpProtobufDecoder.DecodeLogs(Array.Empty<byte>());
        decoded.ResourceLogs.ShouldBeEmpty();
    }

    private static OtlpResource ResourceFor(Guid tenantId, Guid subjectId, string subjectKind)
        => new()
        {
            Attributes =
            {
                new OtlpKeyValue { Key = "sv.tenant.id", Value = new OtlpAnyValue { StringValue = GuidFormatter.Format(tenantId) } },
                new OtlpKeyValue { Key = "sv.subject.uuid", Value = new OtlpAnyValue { StringValue = GuidFormatter.Format(subjectId) } },
                new OtlpKeyValue { Key = "sv.subject.kind", Value = new OtlpAnyValue { StringValue = subjectKind } },
            },
        };
}
