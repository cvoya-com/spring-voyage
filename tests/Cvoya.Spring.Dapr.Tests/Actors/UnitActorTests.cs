// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitActor"/> covering strategy dispatch,
/// control message handling, and member management.
/// </summary>
public class UnitActorTests
{
    private const string TestUnitActorId = "test-unit";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IOrchestrationStrategy _strategy = Substitute.For<IOrchestrationStrategy>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly UnitActor _actor;

    public UnitActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestUnitActorId)
        });
        _actor = new UnitActor(
            host,
            _loggerFactory,
            _strategy,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory);
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

        result.ShouldBe(expectedResponse);
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

        capturedContext.ShouldNotBeNull();
        capturedContext!.UnitAddress.ShouldBe(new Address("unit", "test-unit"));
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

        capturedContext.ShouldNotBeNull();
        capturedContext!.Members.Count().ShouldBe(2);
        capturedContext.Members.ShouldContain(member1);
        capturedContext.Members.ShouldContain(member2);
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

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);
        result.From.ShouldBe(new Address("unit", "test-unit"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Draft");
        payload.GetProperty("MemberCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheck_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_PolicyUpdate_StoresPolicyAndAcknowledges()
    {
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 3 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
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

        result.ShouldNotBeNull();
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

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMembersAsync_WithMembers_ReturnsAllMembers()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("unit", "sub-unit-1");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var result = await _actor.GetMembersAsync(TestContext.Current.CancellationToken);

        result.Count().ShouldBe(2);
        result.ShouldContain(member1);
        result.ShouldContain(member2);
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

        capturedContext.ShouldNotBeNull();
        capturedContext!.UnitAddress.ShouldBe(new Address("unit", "test-unit"));
        capturedContext.Members.ShouldHaveSingleItem().ShouldBe(member1);
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

        capturedContext.ShouldNotBeNull();
        var sendResult = await capturedContext!.SendAsync(message, TestContext.Current.CancellationToken);
        sendResult.ShouldBeNull();
    }

    // --- Error Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_StrategyThrowsException_ReturnsErrorResponse()
    {
        var message = CreateMessage();
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new InvalidOperationException("Strategy failed"));

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString()!.ShouldContain("Strategy failed");
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

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetHumanPermissionAsync_NonExistentHuman_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            StateKeys.HumanPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, UnitPermissionEntry>>(false, default!));

        var result = await _actor.GetHumanPermissionAsync("unknown", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
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

        result.Count().ShouldBe(2);
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

        result.ShouldBeNull();
    }

    // --- Lifecycle Status Tests ---

    [Fact]
    public async Task GetStatusAsync_NewUnit_ReturnsDraft()
    {
        var status = await _actor.GetStatusAsync(TestContext.Current.CancellationToken);

        status.ShouldBe(UnitStatus.Draft);
    }

    [Fact]
    public async Task TransitionAsync_DraftToStopped_SucceedsAndPersists()
    {
        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);
        result.RejectionReason.ShouldBeNull();

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

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Starting);
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

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Running);
    }

    [Fact]
    public async Task TransitionAsync_RunningToStopping_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        var result = await _actor.TransitionAsync(UnitStatus.Stopping, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopping);
    }

    [Fact]
    public async Task TransitionAsync_StoppingToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Stopping));

        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_ErrorToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Error));

        var result = await _actor.TransitionAsync(UnitStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_StartingToError_Succeeds()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Starting));

        var result = await _actor.TransitionAsync(UnitStatus.Error, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Error);
    }

    [Fact]
    public async Task TransitionAsync_RunningToDraft_Rejected()
    {
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(true, UnitStatus.Running));

        var result = await _actor.TransitionAsync(UnitStatus.Draft, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Running);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("Running");
        result.RejectionReason.ShouldContain("Draft");

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

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Stopped);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
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

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().ShouldBe("Running");
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

        metadata.ShouldNotBeNull();
        metadata.DisplayName.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Model.ShouldBeNull();
        metadata.Color.ShouldBeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsPersistedModelAndColor()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "gpt-4o"));
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitColor, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "#ff8800"));

        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Model.ShouldBe("gpt-4o");
        metadata.Color.ShouldBe("#ff8800");
        // DisplayName and Description live on the directory entity, not the actor.
        metadata.DisplayName.ShouldBeNull();
        metadata.Description.ShouldBeNull();
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

    // --- Nested Unit Membership / Cycle Detection Tests (#98) ---

    private static DirectoryEntry MakeUnitEntry(string unitPath, string actorId) =>
        new(
            new Address("unit", unitPath),
            actorId,
            unitPath,
            $"Unit {unitPath}",
            null,
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_PersistsMember()
    {
        // Sub-unit "team-b" has no unit-members of its own, so adding it is safe.
        var subAddress = new Address("unit", "team-b");
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", "team-b-actor"));

        var subProxy = Substitute.For<IUnitActor>();
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "team-b-actor"),
                nameof(UnitActor))
            .Returns(subProxy);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == subAddress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByActorAddress_Throws()
    {
        // The actor's own Address is unit://{actorId} since Id.GetId() == "test-unit".
        var selfAddress = new Address("unit", TestUnitActorId);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(selfAddress, TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(selfAddress);
        ex.ParentUnit.ShouldBe(new Address("unit", TestUnitActorId));
        ex.CyclePath.ShouldNotBeEmpty();

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByPathAddress_Throws()
    {
        // Caller uses the path-form ("my-team") but it resolves to this same
        // actor id — the directory is the tiebreaker, so we must still reject.
        var pathAddress = new Address("unit", "my-team");
        _directoryService.ResolveAsync(pathAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("my-team", TestUnitActorId));

        var selfProxy = Substitute.For<IUnitActor>();
        selfProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Any<ActorId>(),
                nameof(UnitActor))
            .Returns(selfProxy);

        await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(pathAddress, TestContext.Current.CancellationToken));

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_TwoCycle_Throws()
    {
        // Scenario: B already contains A. Adding B to A must be rejected
        // because the resulting graph would close A -> B -> A.
        // This actor is "A" (actor id "test-unit").
        var bAddress = new Address("unit", "team-b");
        _directoryService.ResolveAsync(bAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", "b-actor"));

        var bProxy = Substitute.For<IUnitActor>();
        bProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "team-a") });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "b-actor"),
                nameof(UnitActor))
            .Returns(bProxy);

        // "team-a" resolves back to this actor ("test-unit").
        var aAddress = new Address("unit", "team-a");
        _directoryService.ResolveAsync(aAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-a", TestUnitActorId));

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(bAddress, TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(bAddress);
        ex.Message.ShouldContain("cycle");
        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(2);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_DeepCycle_Throws()
    {
        // Scenario: C -> B -> A. Adding C to A must be rejected.
        // This actor is "A" (actor id "test-unit").
        var cAddress = new Address("unit", "team-c");
        _directoryService.ResolveAsync(cAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-c", "c-actor"));

        var cProxy = Substitute.For<IUnitActor>();
        cProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "team-b") });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "c-actor"),
                nameof(UnitActor))
            .Returns(cProxy);

        var bAddress = new Address("unit", "team-b");
        _directoryService.ResolveAsync(bAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", "b-actor"));

        var bProxy = Substitute.For<IUnitActor>();
        bProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "team-a") });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "b-actor"),
                nameof(UnitActor))
            .Returns(bProxy);

        // "team-a" resolves back to "test-unit" (this actor).
        var aAddress = new Address("unit", "team-a");
        _directoryService.ResolveAsync(aAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-a", TestUnitActorId));

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(cAddress, TestContext.Current.CancellationToken));

        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(3);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_SkipsCycleDetection()
    {
        // Agent members are leaves and cannot introduce cycles. The directory
        // must not be touched for agent-typed adds — assert that by leaving
        // the substitute with no configured behaviour (returns null) and
        // verifying the agent is persisted anyway.
        var agentAddress = new Address("agent", "agent-1");

        await _actor.AddMemberAsync(agentAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == agentAddress),
            Arg.Any<CancellationToken>());

        await _directoryService.DidNotReceive().ResolveAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_SubUnitResolutionFails_TreatsAsDeadEnd()
    {
        // A sub-unit that has been deleted mid-walk must not block the add.
        var subAddress = new Address("unit", "ghost-team");
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == subAddress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_GetMembersThrows_TreatsAsDeadEnd()
    {
        var subAddress = new Address("unit", "flaky-team");
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("flaky-team", "flaky-actor"));

        var flakyProxy = Substitute.For<IUnitActor>();
        flakyProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns<Address[]>(_ => throw new InvalidOperationException("actor unavailable"));
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "flaky-actor"),
                nameof(UnitActor))
            .Returns(flakyProxy);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == subAddress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_BenignSubGraphCycle_DoesNotFalsePositive()
    {
        // The sub-graph below the candidate may itself be cyclic (e.g. a
        // pre-existing bad state). We only care whether the new edge would
        // close a cycle back to *this* unit. A side-cycle that does not
        // involve this unit must not block the add.
        var subAddress = new Address("unit", "team-x");
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-x", "x-actor"));

        var xProxy = Substitute.For<IUnitActor>();
        xProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "team-y") });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "x-actor"),
                nameof(UnitActor))
            .Returns(xProxy);

        // team-y -> team-x (benign 2-cycle in the subgraph, not involving "test-unit").
        var yAddress = new Address("unit", "team-y");
        _directoryService.ResolveAsync(yAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-y", "y-actor"));

        var yProxy = Substitute.For<IUnitActor>();
        yProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "team-x") });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == "y-actor"),
                nameof(UnitActor))
            .Returns(yProxy);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == subAddress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_UnitMember_RemovesWithoutCycleCheck()
    {
        var subAddress = new Address("unit", "team-b");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [subAddress]));

        await _actor.RemoveMemberAsync(subAddress, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());

        // Remove does not walk the graph.
        await _directoryService.DidNotReceive().ResolveAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_WithMixedAgentAndUnitMembers_PassesBothToStrategy()
    {
        // Mixed members: one agent, one unit. Routing fans out to both.
        var agent = new Address("agent", "agent-1");
        var unit = new Address("unit", "team-b");
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent, unit]));

        var message = CreateMessage();
        IUnitContext? captured = null;
        _strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<IUnitContext>(1);
                return Task.FromResult<Message?>(null);
            });

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Members.ShouldContain(agent);
        captured.Members.ShouldContain(unit);
    }

    // #368 — Compound Draft → Starting transition

    [Fact]
    public async Task TransitionAsync_DraftToStarting_WithModel_Succeeds()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "claude-sonnet-4-20250514"));

        var result = await _actor.TransitionAsync(UnitStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(UnitStatus.Starting);

        // Compound transition writes Stopped then Starting.
        Received.InOrder(() =>
        {
            _stateManager.SetStateAsync(
                StateKeys.UnitStatus, UnitStatus.Stopped, Arg.Any<CancellationToken>());
            _stateManager.SetStateAsync(
                StateKeys.UnitStatus, UnitStatus.Starting, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task TransitionAsync_DraftToStarting_WithoutModel_Fails()
    {
        // Default: no model set.
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));

        var result = await _actor.TransitionAsync(UnitStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Draft);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("model");
    }

    [Fact]
    public async Task TransitionAsync_DraftToStarting_EmptyModel_Fails()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "  "));

        var result = await _actor.TransitionAsync(UnitStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason!.ShouldContain("model");
    }

    // #368 — Readiness check

    [Fact]
    public async Task CheckReadinessAsync_WithModel_ReturnsReady()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(true, "claude-sonnet-4-20250514"));

        var result = await _actor.CheckReadinessAsync(TestContext.Current.CancellationToken);

        result.IsReady.ShouldBeTrue();
        result.MissingRequirements.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckReadinessAsync_WithoutModel_ReturnsNotReady()
    {
        _stateManager.TryGetStateAsync<string>(StateKeys.UnitModel, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));

        var result = await _actor.CheckReadinessAsync(TestContext.Current.CancellationToken);

        result.IsReady.ShouldBeFalse();
        result.MissingRequirements.ShouldContain("model");
    }
}