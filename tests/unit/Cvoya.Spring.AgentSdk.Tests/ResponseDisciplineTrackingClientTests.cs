// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="ResponseDisciplineTrackingClient"/> —
/// the internal flag-flipping wrapper that drives
/// <see cref="SpringAgent.RunWithResponseDisciplineAsync"/> (#2493).
/// Accessed by reflection because the type is intentionally internal —
/// the tests assert the only contract that matters: the flag flips
/// once on the first PostResultAsync, never on Delegate / Fanout.
/// </summary>
public class ResponseDisciplineTrackingClientTests
{
    private sealed class RecordingClient : IOrchestrationClient
    {
        public int PostResultCalls;
        public int DelegateCalls;
        public int FanoutCalls;

        public Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default)
        {
            PostResultCalls++;
            return Task.CompletedTask;
        }

        public Task<DelegateResponse> DelegateAsync(string threadId, string targetUnitId, string prompt, CancellationToken cancellationToken = default)
        {
            DelegateCalls++;
            return Task.FromResult(new DelegateResponse(string.Empty, string.Empty));
        }

        public Task<FanoutResponse> FanoutAsync(string threadId, IReadOnlyList<string> targetUnitIds, string prompt, CancellationToken cancellationToken = default)
        {
            FanoutCalls++;
            return Task.FromResult(new FanoutResponse(Array.Empty<FanoutResult>()));
        }
    }

    private static IOrchestrationClient NewTracker(IOrchestrationClient inner)
    {
        var type = typeof(SpringAgent).Assembly
            .GetType("Cvoya.Spring.AgentSdk.ResponseDisciplineTrackingClient", throwOnError: true)!;
        return (IOrchestrationClient)Activator.CreateInstance(type, new object[] { inner })!;
    }

    private static bool ResultPosted(IOrchestrationClient tracker)
    {
        var prop = tracker.GetType().GetProperty("ResultPosted")!;
        return (bool)prop.GetValue(tracker)!;
    }

    [Fact]
    public async Task PostResult_FlipsTracker()
    {
        var inner = new RecordingClient();
        var tracker = NewTracker(inner);

        ResultPosted(tracker).ShouldBeFalse();
        await tracker.PostResultAsync(
            Guid.NewGuid().ToString("D"), "hello", TestContext.Current.CancellationToken);

        ResultPosted(tracker).ShouldBeTrue();
        inner.PostResultCalls.ShouldBe(1);
    }

    [Fact]
    public async Task DelegateAsync_DoesNotFlipTracker()
    {
        var inner = new RecordingClient();
        var tracker = NewTracker(inner);

        await tracker.DelegateAsync(
            Guid.NewGuid().ToString("D"), "unit:123", "prompt", TestContext.Current.CancellationToken);

        // Delegation does not satisfy the response-discipline contract —
        // delegation is a sub-call, not the final reply to the requester.
        ResultPosted(tracker).ShouldBeFalse();
        inner.DelegateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task FanoutAsync_DoesNotFlipTracker()
    {
        var inner = new RecordingClient();
        var tracker = NewTracker(inner);

        await tracker.FanoutAsync(
            Guid.NewGuid().ToString("D"), new[] { "unit:123" }, "prompt", TestContext.Current.CancellationToken);

        ResultPosted(tracker).ShouldBeFalse();
        inner.FanoutCalls.ShouldBe(1);
    }
}
