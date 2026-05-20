// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentActor"/> covering per-thread channel
/// routing, control priority, dispatch lifecycle, and cancel handling
/// (#2076 / ADR-0030 §3 §44).
/// </summary>
public class AgentActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitPolicyEnforcer _unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
    private readonly AgentActor _actor;

    public AgentActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            _loggerFactory,
            NullMessageWriterScopeFactory.Create());
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestSlugIds.HexFor("test-agent"))
        });
        _membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);
        _unitPolicyEnforcer.WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            _loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(new InMemoryAgentLiveConfigStore(), Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));
        SetStateManager(_actor, _stateManager);

        // Default: no per-thread channels, no channel index.
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-sender")),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            type,
            threadId ?? Guid.NewGuid().ToString(),
            payload ?? JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field is not null)
        {
            field.SetValue(actor, stateManager);
        }
        else
        {
            var prop = typeof(Actor).GetProperty("StateManager");
            prop?.SetValue(actor, stateManager);
        }
    }

    // --- Per-thread channel routing (#2076 / ADR-0030 §3 §44) ---

    [Fact]
    public async Task ReceiveAsync_FirstDomainMessage_CreatesPerThreadChannel()
    {
        var threadId = "conv-first";
        var message = CreateMessage(threadId: threadId);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Dispatching),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SameThreadIdWhileDispatching_AppendsToChannel()
    {
        var threadId = "conv-existing";
        var existingChannel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)],
            Dispatching = true,
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, existingChannel));

        var newMessage = CreateMessage(threadId: threadId);
        var result = await _actor.ReceiveAsync(newMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c =>
                c.ThreadId == threadId &&
                c.Messages.Count == 2 &&
                c.Dispatching),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DifferentThreadId_CreatesIndependentChannel()
    {
        // ADR-0030 §44: an in-flight dispatch on thread A must not block
        // a fresh inbound on thread B.
        var threadA = "thread-a";
        var threadB = "thread-b";
        var channelA = new ThreadChannel
        {
            ThreadId = threadA,
            Messages = [CreateMessage(threadId: threadA)],
            Dispatching = true,
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadA, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channelA));

        var inboundB = CreateMessage(threadId: threadB);
        await _actor.ReceiveAsync(inboundB, TestContext.Current.CancellationToken);

        // A new channel for B exists.
        await _stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadB,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadB && c.Dispatching),
            Arg.Any<CancellationToken>());
    }

    // --- Control / status / lifecycle ---

    [Fact]
    public async Task ReceiveAsync_StatusQueryWhenIdle_ReturnsIdleAndEmptyDepths()
    {
        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Idle");
        payload.GetProperty("ThreadDepths").EnumerateObject().Count().ShouldBe(0);
    }

    [Fact]
    public async Task ReceiveAsync_StatusQueryWithActiveThreads_ReportsPerThreadDepth()
    {
        var t1 = new ThreadChannel
        {
            ThreadId = "conv-active",
            Messages = [CreateMessage(threadId: "conv-active"), CreateMessage(threadId: "conv-active")],
            Dispatching = true,
        };

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, ["conv-active"]));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + "conv-active", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, t1));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Active");
        payload.GetProperty("ThreadDepths").GetProperty("conv-active").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheckMessage_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_PolicyUpdateMessage_StoresPolicy()
    {
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 5 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.Received(1).SetStateAsync(
            "Agent:LastPolicyUpdate",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    // --- Runtime-status snapshot (#2100) ---

    [Fact]
    public async Task GetRuntimeStatusAsync_NoChannels_ReportsZero()
    {
        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(0);
        report.QueuedMessageCount.ShouldBe(0);
        report.ChannelCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetRuntimeStatusAsync_OneDispatchingChannel_CountsInFlight()
    {
        var threadId = "conv-busy";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)],
            Dispatching = true,
        };

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, [threadId]));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(1);
        report.QueuedMessageCount.ShouldBe(0);
        report.ChannelCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetRuntimeStatusAsync_DispatchingChannelWithBacklog_CountsQueueBeyondHead()
    {
        var threadId = "conv-busy";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages =
            [
                CreateMessage(threadId: threadId),
                CreateMessage(threadId: threadId),
                CreateMessage(threadId: threadId),
            ],
            Dispatching = true,
        };

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, [threadId]));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        // 3 messages, 1 in-flight head ⇒ 2 queued behind it.
        report.InFlightThreadCount.ShouldBe(1);
        report.QueuedMessageCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetRuntimeStatusAsync_NonDispatchingChannelWithMessages_CountsAllAsQueued()
    {
        // Transient state between drains: channel exists with messages
        // but the dispatcher has just exited and the next one hasn't
        // started yet. Every message is queued ahead of any future drain.
        var threadId = "conv-queued";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages =
            [
                CreateMessage(threadId: threadId),
                CreateMessage(threadId: threadId),
            ],
            Dispatching = false,
        };

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, [threadId]));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(0);
        report.QueuedMessageCount.ShouldBe(2);
    }

    // --- Per-thread cancel (#2076 / ADR-0030 §44) ---

    [Fact]
    public async Task ReceiveAsync_CancelMessage_ReturnsAcknowledgment()
    {
        var threadId = "conv-cancel";
        var cancelMessage = CreateMessage(type: MessageType.Cancel, threadId: threadId);

        var result = await _actor.ReceiveAsync(cancelMessage, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_CancelOneThread_LeavesOtherThreadAlone()
    {
        // ADR-0030 §44: cancel is per-thread. The cancelled thread's
        // channel is removed; other threads' channels are untouched.
        var threadA = "thread-a";
        var threadB = "thread-b";
        var channelA = new ThreadChannel { ThreadId = threadA, Messages = [], Dispatching = true };
        var channelB = new ThreadChannel { ThreadId = threadB, Messages = [], Dispatching = true };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadA, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channelA));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadB, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channelB));

        var cancelA = CreateMessage(type: MessageType.Cancel, threadId: threadA);
        await _actor.ReceiveAsync(cancelA, TestContext.Current.CancellationToken);

        // A's channel removed.
        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadA, Arg.Any<CancellationToken>());
        // B's channel untouched.
        await _stateManager.DidNotReceive().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadB, Arg.Any<CancellationToken>());
    }

    // --- Clone awareness ---

    [Fact]
    public async Task IsCloneAsync_NoCloneIdentity_ReturnsFalse()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.IsCloneAsync(TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsCloneAsync_HasCloneIdentity_ReturnsTrue()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.IsCloneAsync(TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCloneIdentityAsync_NoCloneIdentity_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.GetCloneIdentityAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCloneIdentityAsync_HasCloneIdentity_ReturnsIdentity()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.GetCloneIdentityAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.ParentAgentId.ShouldBe("parent-agent");
        result.CloneId.ShouldBe("test-agent");
        result.CloningPolicy.ShouldBe(CloningPolicy.EphemeralWithMemory);
        result.AttachmentMode.ShouldBe(AttachmentMode.Attached);
    }

    [Fact]
    public async Task GetCostAttributionTargetAsync_IsClone_ReturnsParentId()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        var result = await _actor.GetCostAttributionTargetAsync(TestContext.Current.CancellationToken);

        result.ShouldBe("parent-agent");
    }

    [Fact]
    public async Task GetCostAttributionTargetAsync_NotClone_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        var result = await _actor.GetCostAttributionTargetAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    // --- Activity event emission ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_EmitsMessageReceivedActivityEvent()
    {
        var message = CreateMessage(threadId: "conv-activity");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_ControlMessage_EmitsMessageReceivedActivityEvent()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_MessageReceived_StampsEnvelopeAndBodyOnDetails()
    {
        var threadId = "conv-1209";
        var payload = JsonSerializer.SerializeToElement("hello world");
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Details.HasValue
                && e.Details.Value.GetProperty("messageId").GetString() == message.Id.ToString()
                && e.Details.Value.GetProperty("from").GetString() == $"{message.From.Scheme}://{message.From.Path}"
                && e.Details.Value.GetProperty("to").GetString() == $"{message.To.Scheme}://{message.To.Path}"
                && e.Details.Value.GetProperty("body").GetString() == "hello world"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithStringPayload_SummaryIsBodyText()
    {
        var threadId = "conv-1636-string";
        var payload = JsonSerializer.SerializeToElement("Approve merge?");
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Summary == "Approve merge?"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithAgentReplyShape_SummaryIsOutputText()
    {
        var threadId = "conv-1636-output";
        var payload = JsonSerializer.SerializeToElement(new
        {
            Output = "Looks good — shipping.",
            ExitCode = 0,
        });
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Summary == "Looks good — shipping."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_MessageReceived_SummaryNeverContainsLegacyEnvelopeTemplate()
    {
        var threadId = "conv-1636-no-envelope";
        var payload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && !e.Summary.StartsWith("Received ")
                && !e.Summary.Contains(message.Id.ToString())
                && !e.Summary.Contains(message.From.Path)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_ActivityEventBusFailure_DoesNotBreakActor()
    {
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Bus down")));

        var message = CreateMessage(threadId: "conv-bus-fail");

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_NewThread_EmitsThreadStartedEvent()
    {
        var message = CreateMessage(threadId: "conv-started");

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadStarted &&
                e.CorrelationId == "conv-started"),
            Arg.Any<CancellationToken>());
    }

    // --- Cost-incurred ---

    [Fact]
    public async Task EmitCostIncurredAsync_EmitsCostEvent()
    {
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(false, default!));

        await _actor.EmitCostIncurredAsync(
            0.05m, "gpt-4", 1000, 500,
            Cvoya.Spring.Core.Costs.CostSource.Work,
            TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.CostIncurred &&
                e.Cost == 0.05m &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("costSource").GetString() == "Work"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitCostIncurredAsync_Clone_IncludesParentAgentInDetails()
    {
        var identity = new CloneIdentity("parent-agent", "test-agent",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);
        _stateManager.TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<CloneIdentity>(true, identity));

        await _actor.EmitCostIncurredAsync(
            0.10m, "claude-3", 2000, 1000,
            Cvoya.Spring.Core.Costs.CostSource.Initiative,
            TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.CostIncurred &&
                e.Cost == 0.10m &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("parentAgentId").GetString() == "parent-agent" &&
                e.Details.Value.GetProperty("costSource").GetString() == "Initiative"),
            Arg.Any<CancellationToken>());
    }

    // --- Dispatch lifecycle ---

    [Fact]
    public async Task RunDispatchAsync_NonZeroExitCode_EmitsErrorAndRoutesResponse()
    {
        var threadId = "conv-exit-125";
        var inbound = CreateMessage(threadId: threadId);
        var failurePayload = JsonSerializer.SerializeToElement(new
        {
            Error = "container init: image not found\nlayer 1 missing",
            Output = string.Empty,
            ExitCode = 125,
        });
        var failureResponse = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            inbound.From,
            MessageType.Domain,
            threadId,
            failurePayload,
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(failureResponse);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null));

        // First read returns no channel so the actor takes the create-and-dispatch
        // path. Subsequent reads (during OnDispatchExitAsync) return the
        // channel marked dispatching so the drain can run.
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [inbound],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, channel));

        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ErrorOccurred &&
                e.CorrelationId == threadId &&
                e.Summary.Contains("125") &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("exitCode").GetInt32() == 125),
            Arg.Any<CancellationToken>());

        // Original sender still gets the failure response routed back.
        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == failureResponse.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDispatchAsync_SuccessfulDispatch_DrainsChannel()
    {
        var threadId = "conv-success";
        var inbound = CreateMessage(threadId: threadId);
        var successPayload = JsonSerializer.SerializeToElement(new { Output = "ok" });
        var successResponse = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            inbound.From,
            MessageType.Domain,
            threadId,
            successPayload,
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(successResponse);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null));

        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [inbound],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, channel));
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, [threadId]));

        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // Per-thread channel removed after the queue drained to empty.
        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>());

        // Original sender still gets the reply routed back.
        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == successResponse.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDispatchAsync_CancelledDispatch_DrainsChannel()
    {
        var threadId = "conv-cancelled";
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new OperationCanceledException("simulated worker timeout"));

        var inbound = CreateMessage(threadId: threadId);
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [inbound],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(
                new ConditionalValue<ThreadChannel>(false, default!),
                new ConditionalValue<ThreadChannel>(true, channel));
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, [threadId]));

        await _actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>());
    }

    // --- ErrorOccurred structured details (#2551) ---

    [Fact]
    public async Task ReceiveAsync_UnhandledExceptionDuringMessageHandling_EmitsErrorOccurredWithStructuredDetails()
    {
        // Arrange: make state-manager throw a non-SpringException during domain
        // message handling (TryGetStateAsync<ThreadChannel> is called by
        // GetChannelAsync inside HandleDomainMessageAsync).
        var threadId = "conv-error-details";
        var message = CreateMessage(threadId: threadId);
        const string errorText = "state backend unavailable";

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns<ConditionalValue<ThreadChannel>>(_ => throw new InvalidOperationException(errorText));

        // Act
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Assert: ErrorOccurred must carry { error, agentId, threadId } so the
        // portal activity feed can surface the failure without raw log access
        // (#2551).
        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ErrorOccurred &&
                e.Severity == ActivitySeverity.Error &&
                e.CorrelationId == threadId &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("error").GetString() == errorText &&
                e.Details.Value.GetProperty("agentId").GetString() == TestSlugIds.HexFor("test-agent") &&
                e.Details.Value.GetProperty("threadId").GetString() == threadId),
            Arg.Any<CancellationToken>());
    }

}
