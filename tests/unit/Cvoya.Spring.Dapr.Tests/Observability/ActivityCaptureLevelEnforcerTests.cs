// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Observability;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — server-side capture-level enforcement (truncation
/// applied at ingest, before persistence and SSE delivery).
/// </summary>
public class ActivityCaptureLevelEnforcerTests
{
    [Fact]
    public void Apply_Full_ReturnsPayloadUnchanged()
    {
        var longString = new string('x', 8000);
        var input = JsonSerializer.SerializeToElement(new { prompt = longString });

        var output = ActivityCaptureLevelEnforcer.Apply(input, ActivityCaptureLevel.Full);

        output.GetProperty("prompt").GetString().ShouldBe(longString);
        output.TryGetProperty("truncated", out _).ShouldBeFalse();
    }

    [Fact]
    public void Apply_Summary_LongString_Truncated()
    {
        var longString = new StringBuilder()
            .Append('A', ActivityCaptureLevelEnforcer.HeadCharacters + ActivityCaptureLevelEnforcer.TailCharacters + 100)
            .ToString();
        var input = JsonSerializer.SerializeToElement(new { prompt = longString });

        var output = ActivityCaptureLevelEnforcer.Apply(input, ActivityCaptureLevel.Summary);

        var truncatedValue = output.GetProperty("prompt").GetString();
        truncatedValue.ShouldNotBeNull();
        truncatedValue!.Length.ShouldBeLessThan(longString.Length);
        truncatedValue.ShouldContain("[…]");
        output.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Apply_Summary_ShortString_NotTruncated()
    {
        var input = JsonSerializer.SerializeToElement(new { prompt = "short prompt" });

        var output = ActivityCaptureLevelEnforcer.Apply(input, ActivityCaptureLevel.Summary);

        output.GetProperty("prompt").GetString().ShouldBe("short prompt");
        output.TryGetProperty("truncated", out _).ShouldBeFalse();
    }

    [Fact]
    public void Apply_Off_Throws()
    {
        var input = JsonSerializer.SerializeToElement(new { prompt = "anything" });

        Should.Throw<InvalidOperationException>(() =>
            ActivityCaptureLevelEnforcer.Apply(input, ActivityCaptureLevel.Off));
    }

    [Fact]
    public void Apply_Summary_NestedLongString_TruncatedWithParentMarker()
    {
        var longString = new string('Z', 5000);
        var input = JsonSerializer.SerializeToElement(new
        {
            request = new { prompt = longString, model = "gpt-4" },
        });

        var output = ActivityCaptureLevelEnforcer.Apply(input, ActivityCaptureLevel.Summary);

        var nested = output.GetProperty("request");
        var promptStr = nested.GetProperty("prompt").GetString();
        promptStr.ShouldNotBeNull();
        promptStr!.ShouldContain("[…]");
        nested.GetProperty("model").GetString().ShouldBe("gpt-4");
        nested.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }
}
