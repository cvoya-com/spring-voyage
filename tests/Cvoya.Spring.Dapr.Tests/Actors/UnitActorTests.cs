// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using FluentAssertions;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitActor"/> covering strategy dispatch,
/// control message handling, and member management.
/// </summary>
public class UnitActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IOrchestrationStrategy _strategy = Substitute.For<IOrchestrationStrategy>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly UnitActor _actor;

    public UnitActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-unit")
        });
        _actor = new UnitActor(host, _loggerFactory, _strategy, _activityEventBus);
        SetStateManager(_actor, _stateManager);

        // Default: no members.
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(false, default!));

        // Default: no persisted status -> Draft.
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(false, default));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? conversationId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("unit", "test-unit"),
            type,
            conversationId ?? Guid.NewGuid().ToString(),
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

    // --- Strategy Dispatch Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_DelegatesToOrchestrationStrategy()
    {
        var message = CreateMessage();
        var expectedResponse = CreateMessage(conversationId: message.ConversationId);
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().Be(expectedResponse);
        await _strategy.Received(1).OrchestrateAsync(
            message,
            Arg.Any<IUnitContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_PassesUnitContextWithCorrectAddress()
    {
        var message = CreateMessage();
        IUnitContext? capturedContext = null;
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        capturedContext!.UnitAddress.Should().Be(new Address("unit", "test-unit"));
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_PassesCurrentMembersToStrategy()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var message = CreateMessage();
        IUnitContext? capturedContext = null;
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        capturedContext!.Members.Should().HaveCount(2);
        capturedContext.Members.Should().Contain(member1);
        capturedContext.Members.Should().Contain(member2);
    }

    // --- Control Message Tests ---

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsUnitStatusWithMemberCount()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.StatusQuery);
        result.From.Should().Be(new Address("unit", "test-unit"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Draft");
        payload.GetProperty("MemberCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheck_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_PolicyUpdate_StoresPolicyAndAcknowledges()
    {
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 3 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Policies,
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_CancelMessage_ReturnsAcknowledgment()
    {
        var message = CreateMessage(type: MessageType.Cancel);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
    }

    // --- Member Management Tests ---

    [Fact]
    public async Task AddMemberAsync_NewMember_AddsMemberToState()
    {
        var member = new Address("agent", "new-agent");

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == member),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_DoesNotAddAgain()
    {
        var member = new Address("agent", "existing-agent");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member]));

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_RemovesMemberFromState()
    {
        var member = new Address("agent", "agent-to-remove");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member]));

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_NonExistentMember_DoesNotModifyState()
    {
        var member = new Address("agent", "non-existent");

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMembersAsync_NoMembers_ReturnsEmptyList()
    {
        var result = await _actor.GetMembersAsync(TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMembersAsync_WithMembers_ReturnsAllMembers()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("unit", "sub-unit-1");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var result = await _actor.GetMembersAsync(TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Should().Contain(member1);
        result.Should().Contain(member2);
    }

    // --- UnitContext Tests ---

    [Fact]
    public async Task UnitContext_ExposesCorrectAddressAndMembers()
    {
        var member1 = new Address("agent", "agent-1");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1]));

        var message = CreateMessage();
        IUnitContext? capturedContext = null;
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        capturedContext!.UnitAddress.Should().Be(new Address("unit", "test-unit"));
        capturedContext.Members.Should().ContainSingle().Which.Should().Be(member1);
    }

    [Fact]
    public async Task UnitContext_SendAsync_ReturnsNull_WhenMessageRouterNotAvailable()
    {
        var message = CreateMessage();
        IUnitContext? capturedContext = null;
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        capturedContext.Should().NotBeNull();
        var sendResult = await capturedContext!.SendAsync(message, TestContext.Current.CancellationToken);
        sendResult.Should().BeNull();
    }

    // --- Error Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_StrategyThrowsException_ReturnsErrorResponse()
    {
        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new InvalidOperationException("Strategy failed"));

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString().Should().Contain("Strategy failed");
    }

    // --- Human Permission Tests ---

    [Fact]
    public async Task SetHumanPermissionAsync_NewHuman_StoresPermissionEntry()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            StateKeys.HumanPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, UnitPermissionEntry>>(false, default!));

        var entry = new UnitPermissionEntry("human-1", PermissionLevel.Operator, "Alice", true);
        await _actor.SetHumanPermissionAsync("human-1", entry, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanPermissions,
            Arg.Is<Dictionary<string, UnitPermissionEntry>>(d =>
                d.ContainsKey("human-1") && d["human-1"].Permission == PermissionLevel.Operator),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHumanPermissionAsync_ExistingHuman_ReturnsPermissionLevel()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Owner, "Alice", true)
        };
        _stateManager.TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            StateKeys.HumanPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, UnitPermissionEntry>>(true, permissions));

        var result = await _actor.GetHumanPermissionAsync("human-1", TestContext.Current.CancellationToken);

        result.Should().Be(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetHumanPermissionAsync_NonExistentHuman_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            StateKeys.HumanPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, UnitPermissionEntry>>(false, default!));

        var result = await _actor.GetHumanPermissionAsync("unknown", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHumanPermissionsAsync_MultipleHumans_ReturnsAllEntries()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Owner, "Alice", true),
            ["human-2"] = new("human-2", PermissionLevel.Viewer, "Bob", false)
        };
        _stateManager.TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            StateKeys.HumanPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, UnitPermissionEntry>>(true, permissions));

        var result = await _actor.GetHumanPermissionsAsync(TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
    }

    // --- Activity Event Emission Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_EmitsMessageReceivedEvent()
    {
        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_EmitsDecisionMadeEvent()
    {
        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("orchestration strategy")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_NewMember_EmitsStateChangedEvent()
    {
        var member = new Address("agent", "new-agent");

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("added")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_EmitsStateChangedEvent()
    {
        var member = new Address("agent", "agent-to-remove");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member]));

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("removed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_StrategyThrows_EmitsErrorOccurredEvent()
    {
        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new InvalidOperationException("Strategy failed"));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.ErrorOccurred),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_ActivityEventBusFailure_DoesNotBreakActor()
    {
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Bus down")));

        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        // Should not throw even though the bus fails.
        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // --- Lifecycle Status Tests ---

    [Fact]
    public async Task GetStatusAsync_NewUnit_ReturnsDraft()
    {
        var status = await _actor.GetStatusAsync(TestContext.Current.CancellationToken);

        status.Should().Be(UnitStatus.Draft);
    }

    [Fact]
    public async Task TransitionAsync_DraftToStopped_SucceedsAndPersists()
    {
        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Stopped);
        result.RejectionReason.Should().BeNull();

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitStatus,
            UnitStatus.Stopped,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StoppedToStarting_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Stopped));

        var result = await _actor.TransitionAsync(UnitStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Starting);
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitStatus,
            UnitStatus.Starting,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StartingToRunning_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Starting));

        var result = await _actor.TransitionAsync(UnitStatus.Running, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Running);
    }

    [Fact]
    public async Task TransitionAsync_RunningToStopping_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        var result = await _actor.TransitionAsync(UnitStatus.Stopping, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Stopping);
    }

    [Fact]
    public async Task TransitionAsync_StoppingToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Stopping));

        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_ErrorToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Error));

        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_StartingToError_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Starting));

        var result = await _actor.TransitionAsync(UnitStatus.Error, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.CurrentStatus.Should().Be(UnitStatus.Error);
    }

    [Fact]
    public async Task TransitionAsync_RunningToDraft_Rejected()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        var result = await _actor.TransitionAsync(UnitStatus.Draft, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.CurrentStatus.Should().Be(UnitStatus.Running);
        result.RejectionReason.Should().Contain("Running").And.Contain("Draft");

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitStatus,
            Arg.Any<UnitStatus>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StoppedToRunning_Rejected()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Stopped));

        var result = await _actor.TransitionAsync(UnitStatus.Running, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.CurrentStatus.Should().Be(UnitStatus.Stopped);
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransitionAsync_Success_EmitsStateChangedEvent()
    {
        await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("transitioned")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_Rejected_DoesNotEmitStateChangedEvent()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        _activityEventBus.ClearReceivedCalls();

        await _actor.TransitionAsync(UnitStatus.Draft, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("transitioned")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReportsPersistedStatus()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Running");
    }

    // --- Metadata Tests ---

    [Fact]
    public async Task GetMetadataAsync_ReturnsDefaults_WhenNoStateSet()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitColor, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));

        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Should().NotBeNull();
        metadata.DisplayName.Should().BeNull();
        metadata.Description.Should().BeNull();
        metadata.Model.Should().BeNull();
        metadata.Color.Should().BeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsPersistedModelAndColor()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "gpt-4o"));
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitColor, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "#ff8800"));

        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Model.Should().Be("gpt-4o");
        metadata.Color.Should().Be("#ff8800");
        // DisplayName and Description live on the directory entity, not the actor.
        metadata.DisplayName.Should().BeNull();
        metadata.Description.Should().BeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_PersistsNonNullFields_OnlyWritesDirtyKeys()
    {
        var metadata = new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: "claude-opus-4",
            Color: null);

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        // Model was provided -> written.
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitModel,
            "claude-opus-4",
            Arg.Any<CancellationToken>());

        // Color was null -> must not touch that state key.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitColor,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_AllNullFields_WritesNothingAndEmitsNoEvent()
    {
        _activityEventBus.ClearReceivedCalls();

        var metadata = new UnitMetadata(null, null, null, null);

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitModel,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitColor,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_EmitsStateChanged()
    {
        _activityEventBus.ClearReceivedCalls();

        var metadata = new UnitMetadata(null, null, "claude-opus-4", "#336699");

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("metadata")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_IgnoresDisplayNameAndDescription()
    {
        var metadata = new UnitMetadata(
            DisplayName: "Platform Team",
            Description: "Runs the ship",
            Model: null,
            Color: null);

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        // DisplayName/Description live on the directory entity; the actor
        // must not write them to state.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitModel,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitColor,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}