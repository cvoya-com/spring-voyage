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
/// once on the first PostResultAsync, never on Send / Broadcast.
/// </summary>
public class ResponseDisciplineTrackingClientTests
{
    private sealed class RecordingClient : IMessagingClient
    {
        public int PostResultCalls;
        public int SendCalls;
        public int BroadcastCalls;

        public Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default)
        {
            PostResultCalls++;
            return Task.CompletedTask;
        }

        public Task<MessageSendResponse> SendAsync(string threadId, string targetUnitId, string prompt, CancellationToken cancellationToken = default)
        {
            SendCalls++;
            return Task.FromResult(new MessageSendResponse(true, string.Empty, string.Empty, string.Empty));
        }

        public Task<MessageBroadcastResponse> BroadcastAsync(string threadId, IReadOnlyList<string> targetUnitIds, string prompt, CancellationToken cancellationToken = default)
        {
            BroadcastCalls++;
            return Task.FromResult(new MessageBroadcastResponse(string.Empty, string.Empty, Array.Empty<MessageBroadcastDelivery>()));
        }
    }

    private static IMessagingClient NewTracker(IMessagingClient inner)
    {
        var type = typeof(SpringAgent).Assembly
            .GetType("Cvoya.Spring.AgentSdk.ResponseDisciplineTrackingClient", throwOnError: true)!;
        return (IMessagingClient)Activator.CreateInstance(type, new object[] { inner })!;
    }

    private static bool ResultPosted(IMessagingClient tracker)
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
    public async Task SendAsync_DoesNotFlipTracker()
    {
        var inner = new RecordingClient();
        var tracker = NewTracker(inner);

        await tracker.SendAsync(
            Guid.NewGuid().ToString("D"), "unit:123", "prompt", TestContext.Current.CancellationToken);

        // Sending a message does not satisfy the response-discipline
        // contract — it is a delivery, not the final reply to the requester.
        ResultPosted(tracker).ShouldBeFalse();
        inner.SendCalls.ShouldBe(1);
    }

    [Fact]
    public async Task BroadcastAsync_DoesNotFlipTracker()
    {
        var inner = new RecordingClient();
        var tracker = NewTracker(inner);

        await tracker.BroadcastAsync(
            Guid.NewGuid().ToString("D"), new[] { "unit:123" }, "prompt", TestContext.Current.CancellationToken);

        ResultPosted(tracker).ShouldBeFalse();
        inner.BroadcastCalls.ShouldBe(1);
    }
}
