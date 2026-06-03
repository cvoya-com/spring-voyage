// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests verifying agent per-thread channel routing
/// (#2076 / ADR-0030 §3 §44): a fresh inbound creates a per-thread
/// channel, follow-ups append to that channel, and concurrent inbound
/// on a different thread creates an independent channel without
/// queueing as pending behind the first.
/// </summary>
public class AgentMailboxRoutingTests
{
    [Fact]
    public async Task ReceiveAsync_FirstDomainMessage_CreatesPerThreadChannel()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var threadId = "conv-first";
        var message = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "mailbox-agent");

        var result = await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        // #3056: activation persists the channel dispatching with the in-flight
        // batch recorded (one message here) so a later drain removes exactly
        // the dispatched set.
        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Dispatching && c.InFlightCount == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SameThreadIdWhileDispatching_AppendsToChannel()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var threadId = "conv-append";

        // Pre-existing channel marked dispatching — simulates a drain
        // loop already running for this thread.
        var existingChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [MessageFactory.CreateDomainMessage(threadId: threadId)],
            Dispatching = true,
        };

        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, existingChannel));

        var followUp = MessageFactory.CreateDomainMessage(threadId: threadId, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(followUp, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Messages.Count == 2 && c.Dispatching),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DifferentThreadId_CreatesIndependentChannel()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");
        var threadA = "conv-a";
        var threadB = "conv-b";

        // Thread A is in flight. Inbound on thread B must create its own
        // channel — not be queued behind thread A.
        var channelA = new ThreadChannel
        {
            ThreadId = threadA,
            Messages = [MessageFactory.CreateDomainMessage(threadId: threadA)],
            Dispatching = true,
        };
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadA, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channelA));

        var inboundB = MessageFactory.CreateDomainMessage(threadId: threadB, toId: "mailbox-agent");
        var result = await actor.ReceiveAsync(inboundB, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        // A new per-thread channel exists for B, marked dispatching.
        await stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadB,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadB && c.Dispatching),
            Arg.Any<CancellationToken>());
        // No QueueAsPending list write happens — the legacy single-slot
        // gate is gone.
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsPerThreadDepthMap()
    {
        var (actor, stateManager) = ActorTestHost.CreateAgentActor("mailbox-agent");

        // Two active threads: depths {2, 1}.
        var t1 = new ThreadChannel
        {
            ThreadId = "conv-1",
            Messages = [
                MessageFactory.CreateDomainMessage(threadId: "conv-1"),
                MessageFactory.CreateDomainMessage(threadId: "conv-1"),
            ],
            Dispatching = true,
        };
        var t2 = new ThreadChannel
        {
            ThreadId = "conv-2",
            Messages = [MessageFactory.CreateDomainMessage(threadId: "conv-2")],
            Dispatching = true,
        };

        stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, ["conv-1", "conv-2"]));
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + "conv-1", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, t1));
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + "conv-2", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, t2));

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "mailbox-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        var depths = payload.GetProperty("ThreadDepths");
        depths.GetProperty("conv-1").GetInt32().ShouldBe(2);
        depths.GetProperty("conv-2").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveAsync_StatusQueryWhenIdle_ReturnsIdleAndEmptyDepths()
    {
        var (actor, _) = ActorTestHost.CreateAgentActor("idle-agent");

        var statusQuery = MessageFactory.CreateStatusQuery("requester", "idle-agent");
        var result = await actor.ReceiveAsync(statusQuery, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Idle");
        payload.GetProperty("ThreadDepths").EnumerateObject().Count().ShouldBe(0);
    }
}
