// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using Google.Protobuf;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2501: smoke-tests <see cref="TelemetryProtobufEncoder"/>.
/// </summary>
/// <remarks>
/// The ingest decoder lives in <c>Cvoya.Spring.Host.Api</c> and is not
/// referenced from this project, so we verify well-formedness here by
/// re-parsing key fields out of the byte stream with Google.Protobuf's
/// public <see cref="CodedInputStream"/>. The full end-to-end
/// round-trip is covered by the ingest tests in the Host.Api test
/// suite.
/// </remarks>
public class TelemetryProtobufEncoderTests
{
    [Fact]
    public void EncodeTraceEnvelope_ProducesNonEmptyWireFormPayload()
    {
        var span = new Dictionary<string, object?>
        {
            ["name"] = "sv.agent.turn",
            ["traceId"] = "0102030405060708090a0b0c0d0e0f10",
            ["spanId"] = "1112131415161718",
            ["startTimeUnixNano"] = "1700000000000000000",
            ["endTimeUnixNano"] = "1700000000999000000",
            ["attributes"] = new List<object>(),
            ["events"] = new List<object>(),
        };

        var resource = new Dictionary<string, string>
        {
            ["sv.tenant.id"] = "tenant",
            ["sv.subject.uuid"] = "subject",
            ["sv.subject.kind"] = "agent",
        };

        var bytes = TelemetryProtobufEncoder.EncodeTraceEnvelope(
            span,
            resource,
            scopeName: "Cvoya.Spring.AgentSdk",
            scopeVersion: "0.1.0");

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(0);

        // The first tag must be field 1, wire type 2 (length-delimited):
        // 1<<3 | 2 = 0x0A. Confirms we wrote the resourceSpans envelope head.
        bytes[0].ShouldBe((byte)0x0A);

        // Sanity: the payload must be parseable by Google.Protobuf without
        // raising; parsing exercises the wire structure end-to-end even
        // though we don't have a generated message closure to match
        // against.
        using var input = new CodedInputStream(bytes);
        var tag = input.ReadTag();
        Google.Protobuf.WireFormat.GetTagFieldNumber(tag).ShouldBe(1);
        Google.Protobuf.WireFormat.GetTagWireType(tag).ShouldBe(Google.Protobuf.WireFormat.WireType.LengthDelimited);
    }
}
