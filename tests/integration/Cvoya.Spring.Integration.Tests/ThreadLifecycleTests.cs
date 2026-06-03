// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests covering the per-thread channel lifecycle
/// (#2076 / ADR-0030 §3 §44): initial activation, follow-up appending,
/// concurrent threads on the same agent, and per-thread cancel.
/// </summary>
public class ThreadLifecycleTests
{
    [Fact]
    public async Task Lifecycle_InitialMessage_CreatesChannel_FollowUpAppends()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("lifecycle-agent");
        var threadId = "lifecycle-conv-1";

        // Step 1: Send initial domain message — creates the per-thread channel.
        var firstMessage = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "lifecycle-agent");
        var result1 = await actor.ReceiveAsync(firstMessage, TestContext.Current.CancellationToken);
        result1.ShouldNotBeNull();

        // #3056: activation persists the dispatching channel with the in-flight
        // batch size recorded (one message here).
        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Messages.Count == 1 && c.Dispatching && c.InFlightCount == 1),
            Arg.Any<CancellationToken>());

        // Simulate state with the channel mid-drain.
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [firstMessage],
            Dispatching = true,
        };
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        // Step 2: Send follow-up — appended to the same channel.
        var followUpMessage = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "lifecycle-agent");
        var result2 = await actor.ReceiveAsync(followUpMessage, TestContext.Current.CancellationToken);
        result2.ShouldNotBeNull();

        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_ConcurrentThreads_BothDispatchedIndependently()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("lifecycle-agent");
        var threadA = "thread-a";
        var threadB = "thread-b";

        // Thread A is mid-drain.
        var channelA = new ThreadChannel
        {
            ThreadId = threadA,
            Messages = [MessageFactory.CreateDomainMessage(threadId: threadA)],
            Dispatching = true,
        };
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadA, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channelA));

        // Inbound on thread B must create an independent channel that
        // dispatches in parallel — not queue behind A.
        var inboundB = MessageFactory.CreateDomainMessage(threadId: threadB, toId: "lifecycle-agent");
        var result = await actor.ReceiveAsync(inboundB, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();

        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadB,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadB && c.Dispatching),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_CancelThread_RemovesOnlyThatChannel()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("cancel-agent");
        var threadId = "thread-to-cancel";

        // Channel exists for thread-to-cancel.
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [MessageFactory.CreateDomainMessage(threadId: threadId)],
            Dispatching = true,
        };
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        // Cancel the thread.
        var cancelMessage = MessageFactory.CreateCancelMessage(threadId, "requester", "cancel-agent");
        var result = await actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Any<CancellationToken>());
    }
}
