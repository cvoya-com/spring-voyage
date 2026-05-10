// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitActor"/> covering runtime dispatch,
/// control message handling, and member management. Per ADR-0040 /
/// #2049 unit live config (model / color / provider / hosting),
/// boundary, permission inheritance, and own-expertise live in EF; the
/// tests drive that surface through
/// <see cref="InMemoryUnitLiveConfigStore"/>.
/// </summary>
public class UnitActorTests
{
    private static readonly Guid TestUnitGuid = new("aaaaaaaa-0000-0000-0000-000000000010");
    private static readonly string TestUnitActorId = TestUnitGuid.ToString("N");

    // Stable UUID constants for deterministic human-permission tests (#1491).
    private static readonly Guid Human1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Human2 = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid HumanUnknown = new("aaaaaaaa-0000-0000-0000-000000000099");

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IRuntimeInvocationPath _runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitHumanPermissionStore _permissionStore = Substitute.For<IUnitHumanPermissionStore>();
    private readonly InMemoryUnitLiveConfigStore _liveConfigStore = new();
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
            _runtimeInvocationPath,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory,
            new UnitStateCoordinator(_liveConfigStore, Substitute.For<ILogger<UnitStateCoordinator>>()),
            humanPermissionStore: _permissionStore);
        SetStateManager(_actor, _stateManager);

        // Default: no members.
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(false, default!));

        // Default: no persisted status -> Draft.
        _stateManager.TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<UnitStatus>(false, default));

        _runtimeInvocationPath
            .InvokeAsync(Arg.Any<Address>(), Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _stateManager.ClearReceivedCalls();
        _runtimeInvocationPath.ClearReceivedCalls();
        _activityEventBus.ClearReceivedCalls();
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-sender")),
            Address.For("unit", TestUnitActorId),
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

    // --- Runtime Dispatch Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_InvokesRuntimePath()
    {
        var message = CreateMessage();

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestUnitActorId),
            message,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_DoesNotReadMembersBeforeRuntimeInvocation()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().TryGetStateAsync<List<Address>>(
            StateKeys.Members,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_DoesNotConsultOrchestrationStrategy()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Arg.Any<Address>(),
            message,
            Arg.Any<CancellationToken>());
    }

    // --- Control Message Tests ---

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsUnitStatusWithMemberCount()
    {
        var member1 = Address.For("agent", TestSlugIds.HexFor("agent-1"));
        var member2 = Address.For("agent", TestSlugIds.HexFor("agent-2"));
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);
        result.From.ShouldBe(Address.For("unit", TestUnitActorId));

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
    public async Task ReceiveAsync_PolicyUpdate_AcknowledgesWithoutActorStateWrite()
    {
        // ADR-0040 / #2049: PolicyUpdate is now a notification. The
        // actor must not write any actor-state copy of the policy
        // payload (that mirror was dropped); UnitPolicyEntity is the
        // single write path. The message handler still acknowledges
        // and emits an audit event.
        var policyPayload = JsonSerializer.SerializeToElement(new { MaxConcurrency = 3 });
        var message = CreateMessage(type: MessageType.PolicyUpdate, payload: policyPayload);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        await _stateManager.DidNotReceive().SetStateAsync(
            "Unit:Policies",
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
        var member = Address.For("agent", TestSlugIds.HexFor("new-agent"));

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.Members,
            Arg.Is<List<Address>>(list => list.Count == 1 && list[0] == member),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_DoesNotAddAgain()
    {
        var member = Address.For("agent", TestSlugIds.HexFor("existing-agent"));
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
        var member = Address.For("agent", TestSlugIds.HexFor("agent-to-remove"));
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
        var member = Address.For("agent", TestSlugIds.HexFor("non-existent"));

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
        var member1 = Address.For("agent", TestSlugIds.HexFor("agent-1"));
        var member2 = Address.For("unit", TestSlugIds.HexFor("sub-unit-1"));
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var result = await _actor.GetMembersAsync(TestContext.Current.CancellationToken);

        result.Count().ShouldBe(2);
        result.ShouldContain(member1);
        result.ShouldContain(member2);
    }

    // --- Error Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_RuntimePathThrowsException_ReturnsErrorResponse()
    {
        var message = CreateMessage();
        _runtimeInvocationPath
            .InvokeAsync(Arg.Any<Address>(), message, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Runtime path failed"));

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString()!.ShouldContain("Runtime path failed");
    }

    // --- Human Permission Tests (#2044 / ADR-0040) ---
    // ACL grants live in the unit_human_permissions EF table; the actor
    // delegates to IUnitHumanPermissionStore for every read and write and
    // never touches actor state on this path.

    [Fact]
    public async Task SetHumanPermissionAsync_NewHuman_WritesToEfStore()
    {
        var entry = new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Operator, "Alice", true);
        await _actor.SetHumanPermissionAsync(Human1, entry, TestContext.Current.CancellationToken);

        await _permissionStore.Received(1).UpsertAsync(
            TestUnitGuid,
            Human1,
            Arg.Is<UnitPermissionEntry>(e => e.Permission == PermissionLevel.Operator && e.Identity == "Alice"),
            Arg.Any<CancellationToken>());

        // The legacy actor-state key is gone — no SetStateAsync call should
        // ever fire for the dictionary blob the old shape used.
        await _stateManager.DidNotReceive().SetStateAsync(
            "Unit:HumanPermissions",
            Arg.Any<Dictionary<string, UnitPermissionEntry>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHumanPermissionAsync_ExistingHuman_ReturnsPermissionFromEf()
    {
        _permissionStore.GetPermissionAsync(TestUnitGuid, Human1, Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Owner);

        var result = await _actor.GetHumanPermissionAsync(Human1, TestContext.Current.CancellationToken);

        result.ShouldBe(PermissionLevel.Owner);
        await _stateManager.DidNotReceive().TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(
            "Unit:HumanPermissions", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHumanPermissionAsync_NonExistentHuman_ReturnsNull()
    {
        _permissionStore.GetPermissionAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var result = await _actor.GetHumanPermissionAsync(HumanUnknown, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetHumanPermissionsAsync_MultipleHumans_ReturnsEntriesFromEf()
    {
        _permissionStore.ListByUnitAsync(TestUnitGuid, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Owner, "Alice", true),
                new UnitPermissionEntry(Human2.ToString(), PermissionLevel.Viewer, "Bob", false),
            });

        var result = await _actor.GetHumanPermissionsAsync(TestContext.Current.CancellationToken);

        result.Length.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveHumanPermissionAsync_ExistingEntry_DelegatesToEfStore()
    {
        _permissionStore.DeleteAsync(TestUnitGuid, Human1, Arg.Any<CancellationToken>())
            .Returns(true);

        var removed = await _actor.RemoveHumanPermissionAsync(Human1, TestContext.Current.CancellationToken);

        removed.ShouldBeTrue();
        await _permissionStore.Received(1).DeleteAsync(
            TestUnitGuid, Human1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveHumanPermissionAsync_UnknownEntry_IsNoOpAndReturnsFalse()
    {
        // Idempotence is load-bearing: the CLI must not need to branch on
        // "already absent" vs "just removed". The store reports false; the
        // actor surfaces it without touching anything else.
        _permissionStore.DeleteAsync(TestUnitGuid, HumanUnknown, Arg.Any<CancellationToken>())
            .Returns(false);

        var removed = await _actor.RemoveHumanPermissionAsync(HumanUnknown, TestContext.Current.CancellationToken);

        removed.ShouldBeFalse();
    }

    // --- Activity Event Emission Tests ---

    [Fact]
    public async Task ReceiveAsync_DomainMessage_EmitsMessageReceivedEvent()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageReceived),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithStringPayload_SummaryIsBodyText()
    {
        // #1636: production must NEVER write the legacy "Received Domain
        // message <uuid> from <address>" envelope as the activity-event
        // summary. The summary is the message text — never the GUID-bearing
        // envelope template.
        var payload = JsonSerializer.SerializeToElement("Plan the next sprint.");
        var message = CreateMessage(threadId: "conv-1636-unit-string", payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Summary == "Plan the next sprint."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_SummaryNeverContainsLegacyEnvelopeTemplate()
    {
        // #1636: hard regression guard — never start with "Received " and
        // never carry the message GUID or sender address.
        var payload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        var message = CreateMessage(threadId: "conv-1636-unit-no-envelope", payload: payload);

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
    public async Task ReceiveAsync_DomainMessage_DoesNotEmitStrategyDecisionEvent()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_NewMember_EmitsStateChangedEvent()
    {
        var member = Address.For("agent", TestSlugIds.HexFor("new-agent"));

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
        var member = Address.For("agent", TestSlugIds.HexFor("agent-to-remove"));
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
    public async Task ReceiveAsync_RuntimePathThrows_EmitsErrorOccurredEvent()
    {
        var message = CreateMessage();
        _runtimeInvocationPath
            .InvokeAsync(Arg.Any<Address>(), message, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Runtime path failed"));

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

    // --- Metadata Tests (ADR-0040 / #2049: EF-backed) ---

    [Fact]
    public async Task GetMetadataAsync_ReturnsDefaults_WhenNoEfRow()
    {
        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.ShouldNotBeNull();
        metadata.DisplayName.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Model.ShouldBeNull();
        metadata.Color.ShouldBeNull();
        metadata.Provider.ShouldBeNull();
        metadata.Hosting.ShouldBeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsPersistedModelAndColor()
    {
        _liveConfigStore.SeedMetadata(
            TestUnitGuid,
            new UnitMetadata(null, null, "gpt-4o", "#ff8800"));

        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Model.ShouldBe("gpt-4o");
        metadata.Color.ShouldBe("#ff8800");
        // DisplayName and Description live on the directory entity, not the actor.
        metadata.DisplayName.ShouldBeNull();
        metadata.Description.ShouldBeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsPersistedProviderHosting()
    {
        _liveConfigStore.SeedMetadata(
            TestUnitGuid,
            new UnitMetadata(null, null, null, null, "ollama", "ephemeral"));

        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Provider.ShouldBe("ollama");
        metadata.Hosting.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task SetMetadataAsync_PersistsProviderHosting()
    {
        var metadata = new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: null,
            Color: null,
            Provider: "ollama",
            Hosting: "ephemeral");

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetMetadataAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.Provider.ShouldBe("ollama");
        fetched.Hosting.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task SetMetadataAsync_NullProviderHosting_DoesNotTouchEf()
    {
        // A patch that only sets Model must leave Provider / Hosting alone.
        // Seed Provider / Hosting first, then PATCH Model only.
        _liveConfigStore.SeedMetadata(
            TestUnitGuid,
            new UnitMetadata(null, null, null, null, "ollama", "ephemeral"));

        var metadata = new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: "claude-opus-4",
            Color: null);

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetMetadataAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.Model.ShouldBe("claude-opus-4");
        fetched.Provider.ShouldBe("ollama");
        fetched.Hosting.ShouldBe("ephemeral");
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

        var fetched = await _liveConfigStore.GetMetadataAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.Model.ShouldBe("claude-opus-4");
        // Color was null -> must remain unset.
        fetched.Color.ShouldBeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_AllNullFields_WritesNothingAndEmitsNoEvent()
    {
        _activityEventBus.ClearReceivedCalls();

        var metadata = new UnitMetadata(null, null, null, null);

        await _actor.SetMetadataAsync(metadata, TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetMetadataAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.Model.ShouldBeNull();
        fetched.Color.ShouldBeNull();

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

        // DisplayName/Description live on the directory entity; the
        // actor must not write any actor-owned fields to EF on this
        // path. The unit_live_config row should remain untouched.
        var fetched = await _liveConfigStore.GetMetadataAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.Model.ShouldBeNull();
        fetched.Color.ShouldBeNull();
    }

    // --- Nested Unit Membership / Cycle Detection Tests (#98) ---

    private static DirectoryEntry MakeUnitEntry(string unitPath, Guid actorId) =>
        new(
            new Address("unit", actorId),
            actorId,
            unitPath,
            $"Unit {unitPath}",
            null,
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_PersistsMember()
    {
        // Sub-unit "team-b" has no unit-members of its own, so adding it is safe.
        var teamBId = Guid.NewGuid();
        var subAddress = new Address("unit", teamBId);
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", teamBId));

        var subProxy = Substitute.For<IUnitActor>();
        subProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamBId.ToString("N")),
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
        // The actor's own Address is unit://{actorId} since Id.GetId() == TestUnitActorId.
        var selfAddress = new Address("unit", TestUnitGuid);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(selfAddress, TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(selfAddress);
        ex.ParentUnit.ShouldBe(new Address("unit", TestUnitGuid));
        ex.CyclePath.ShouldNotBeEmpty();

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.Members,
            Arg.Any<List<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByPathAddress_Throws()
    {
        // Caller uses a different unit address but it resolves to this same
        // actor id — the directory is the tiebreaker, so we must still reject.
        var aliasId = Guid.NewGuid();
        var pathAddress = new Address("unit", aliasId);
        _directoryService.ResolveAsync(pathAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("my-team", TestUnitGuid));

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
        // This actor is "A" (actor TestUnitActorId).
        var teamBId = Guid.NewGuid();
        var teamAId = TestUnitGuid;
        var bAddress = new Address("unit", teamBId);
        _directoryService.ResolveAsync(bAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", teamBId));

        var bProxy = Substitute.For<IUnitActor>();
        bProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", teamAId) });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamBId.ToString("N")),
                nameof(UnitActor))
            .Returns(bProxy);

        var aAddress = new Address("unit", teamAId);
        _directoryService.ResolveAsync(aAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-a", TestUnitGuid));

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
        var teamCId = Guid.NewGuid();
        var teamBId = Guid.NewGuid();
        var teamAId = TestUnitGuid;
        var cAddress = new Address("unit", teamCId);
        _directoryService.ResolveAsync(cAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-c", teamCId));

        var cProxy = Substitute.For<IUnitActor>();
        cProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", teamBId) });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamCId.ToString("N")),
                nameof(UnitActor))
            .Returns(cProxy);

        var bAddress = new Address("unit", teamBId);
        _directoryService.ResolveAsync(bAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-b", teamBId));

        var bProxy = Substitute.For<IUnitActor>();
        bProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", teamAId) });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamBId.ToString("N")),
                nameof(UnitActor))
            .Returns(bProxy);

        var aAddress = new Address("unit", teamAId);
        _directoryService.ResolveAsync(aAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-a", TestUnitGuid));

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
        var agentAddress = new Address("agent", Guid.NewGuid());

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
        var subAddress = new Address("unit", Guid.NewGuid());
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
        var flakyId = Guid.NewGuid();
        var subAddress = new Address("unit", flakyId);
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("flaky-team", flakyId));

        var flakyProxy = Substitute.For<IUnitActor>();
        flakyProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns<Address[]>(_ => throw new InvalidOperationException("actor unavailable"));
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == flakyId.ToString("N")),
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
        var teamXId = Guid.NewGuid();
        var teamYId = Guid.NewGuid();
        var subAddress = new Address("unit", teamXId);
        _directoryService.ResolveAsync(subAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-x", teamXId));

        var xProxy = Substitute.For<IUnitActor>();
        xProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", teamYId) });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamXId.ToString("N")),
                nameof(UnitActor))
            .Returns(xProxy);

        // team-y -> team-x (benign 2-cycle in the subgraph, not involving the test unit).
        var yAddress = new Address("unit", teamYId);
        _directoryService.ResolveAsync(yAddress, Arg.Any<CancellationToken>())
            .Returns(MakeUnitEntry("team-y", teamYId));

        var yProxy = Substitute.For<IUnitActor>();
        yProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", teamXId) });
        _actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == teamYId.ToString("N")),
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
        var subAddress = Address.For("unit", TestSlugIds.HexFor("team-b"));
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
    public async Task ReceiveAsync_DomainMessage_WithMixedAgentAndUnitMembers_InvokesRuntimePath()
    {
        // Mixed members remain unit state. Domain dispatch now enters the
        // unit runtime; child routing is exposed later through orchestration
        // tools rather than an IUnitContext handed to UnitActor.
        var agent = Address.For("agent", TestSlugIds.HexFor("agent-1"));
        var unit = Address.For("unit", TestSlugIds.HexFor("team-b"));
        _stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [agent, unit]));

        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestUnitActorId),
            message,
            Arg.Any<CancellationToken>());
    }

    // #939 — Draft → Starting is rejected; units must pass through Validating first

    [Fact]
    public async Task TransitionAsync_DraftToStarting_IsRejected()
    {
        // Draft → Starting is no longer a valid transition (#939).
        // Units must go Draft → Validating → Stopped → Starting.
        var result = await _actor.TransitionAsync(UnitStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(UnitStatus.Draft);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("Draft");
        result.RejectionReason.ShouldContain("Starting");
    }

    // #368 — Readiness check (ADR-0040 / #2049: Model lives on EF row)

    [Fact]
    public async Task CheckReadinessAsync_WithModel_ReturnsReady()
    {
        _liveConfigStore.SeedMetadata(
            TestUnitGuid,
            new UnitMetadata(null, null, "claude-sonnet-4-6", null));

        var result = await _actor.CheckReadinessAsync(TestContext.Current.CancellationToken);

        result.IsReady.ShouldBeTrue();
        result.MissingRequirements.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckReadinessAsync_WithoutModel_ReturnsNotReady()
    {
        var result = await _actor.CheckReadinessAsync(TestContext.Current.CancellationToken);

        result.IsReady.ShouldBeFalse();
        result.MissingRequirements.ShouldContain("model");
    }
}
