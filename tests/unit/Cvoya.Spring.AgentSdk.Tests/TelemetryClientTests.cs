// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="TelemetryClient"/> (#2493).
/// </summary>
public class TelemetryClientTests
{
    [Fact]
    public void Disabled_WhenNoEndpoint()
    {
        using var client = new TelemetryClient(endpoint: null);
        client.Enabled.ShouldBeFalse();
        // All emit_* methods are no-ops; ReportProgress passes the rate
        // limiter and returns false from the underlying emitter.
        var ok = client.ReportProgress("hello");
        ok.ShouldBeFalse();
    }

    [Fact]
    public void Enabled_WhenEndpointProvided()
    {
        using var client = new TelemetryClient(
            endpoint: new Uri("https://otel.example/otlp"),
            resourceAttributes: new Dictionary<string, string>
            {
                ["sv.subject.uuid"] = "abc",
                ["sv.subject.kind"] = "agent",
                ["sv.tenant.id"] = "t1",
            });
        client.Enabled.ShouldBeTrue();
        client.SubjectUuid.ShouldBe("abc");
        client.ResourceAttributes["sv.tenant.id"].ShouldBe("t1");
    }

    [Fact]
    public void TraceAndSpan_AreHexAndCorrectlyLength()
    {
        using var client = new TelemetryClient(endpoint: new Uri("https://x/otlp"));
        client.TraceId.Length.ShouldBe(32);
        client.RootSpanId.Length.ShouldBe(16);
        // Hex characters only.
        client.TraceId.ShouldAllBe(c => Uri.IsHexDigit(c));
        client.RootSpanId.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void ReportProgress_RateLimited_ReturnsFalseWhenLimitTripped()
    {
        // 0 endpoint + drained limiter -> all calls drop.
        var limiter = new ProgressRateLimiter(ratePerSecond: 0.01, burst: 1);
        using var client = new TelemetryClient(
            endpoint: null,
            rateLimiter: limiter,
            resourceAttributes: new Dictionary<string, string> { ["sv.subject.uuid"] = "x" });

        // First call passes the limiter but the disabled emitter returns false.
        var first = client.ReportProgress("a");
        // Subsequent ones are dropped at the limiter.
        var second = client.ReportProgress("b");
        var third = client.ReportProgress("c");

        first.ShouldBeFalse();
        second.ShouldBeFalse();
        third.ShouldBeFalse();
    }

    [Fact]
    public void EmitResponseDisciplineViolation_BypassesRateLimit()
    {
        // Drained limiter — report_progress is blocked, but the
        // violation event bypasses and still attempts emission.
        var limiter = new ProgressRateLimiter(ratePerSecond: 0.01, burst: 1);
        // Drain by invoking once.
        limiter.TryAcquire("x", "response_discipline_violation");
        using var client = new TelemetryClient(
            endpoint: null,
            rateLimiter: limiter,
            resourceAttributes: new Dictionary<string, string> { ["sv.subject.uuid"] = "x" });

        // Returns false because no endpoint, but it must NOT throw.
        var ok = client.EmitResponseDisciplineViolation("forgot to reply");
        ok.ShouldBeFalse();
    }

    [Fact]
    public void ToolCallSpan_Disposes_WithoutError()
    {
        using var client = new TelemetryClient(endpoint: null);
        var span = client.ToolCall("acme.echo", new { x = 1 });
        span.TraceId.ShouldBe(client.TraceId);
        span.SetResult("hello");
        span.Dispose(); // Must not throw even when disabled.
    }

    [Fact]
    public void LlmTurnSpan_RecordsCompletionAndDisposes()
    {
        using var client = new TelemetryClient(endpoint: null);
        var span = client.LlmTurn("claude-sonnet", prompt: "hello");
        span.SetCompletion("world", tokensInput: 2, tokensOutput: 1);
        span.Dispose(); // No-op when disabled.
    }
}
