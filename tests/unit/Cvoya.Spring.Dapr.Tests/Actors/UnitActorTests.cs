// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
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
using NSubstitute.ExceptionExtensions;

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
    private readonly InMemoryUnitMemberGraphStore _memberGraphStore = new();
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
            _memberGraphStore,
            humanPermissionStore: _permissionStore);
        SetStateManager(_actor, _stateManager);

        // Default: no persisted status -> Draft.
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(false, default));

        // Default: no per-thread channels, no channel index (#3031 — the unit
        // now drives the same per-thread mailbox as AgentActor).
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));

        // #3031: domain dispatch now flows through the mailbox-aware lean
        // overload (builds the unit's minimal context internally, threads the
        // per-thread drain callback + dispatch token).
        _runtimeInvocationPath
            .InvokeAsync(
                Arg.Any<Address>(),
                Arg.Any<Message>(),
                Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
                Arg.Any<Func<string, Task>>(),
                Arg.Any<CancellationToken>())
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

        // #3031: domain dispatch is fire-and-forget through the per-thread
        // mailbox — ReceiveAsync enqueues and returns null; the runtime is
        // invoked on the background dispatch task. Await it so the assertion
        // is deterministic.
        result.ShouldBeNull();
        await _actor.PendingDispatchTask!;
        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestUnitActorId),
            message,
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_DoesNotReadMembersBeforeRuntimeInvocation()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // ADR-0040 / #2052: domain dispatch goes straight into the
        // runtime path; the actor must not preload the EF member graph
        // for a Domain message that does not need it.
        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Arg.Any<Address>(),
            message,
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_DoesNotConsultUnitStrategy()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Arg.Any<Address>(),
            message,
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_ForwardsNonNullActivityDelegateToRuntimePath()
    {
        // #2211: the unit actor must pass its own activity-emission
        // delegate to the runtime-invocation path so that ErrorOccurred
        // events from the dispatch coordinator (e.g. credential-resolution
        // failures) surface in the unit's Activity feed instead of being
        // silently dropped.
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Arg.Any<Address>(),
            message,
            Arg.Is<Func<ActivityEvent, CancellationToken, Task>>(d => d != null),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    // --- Runtime-status snapshot (#2491) ---

    [Fact]
    public async Task GetRuntimeStatusAsync_NoActiveWork_ReportsZero()
    {
        // Pre-#2491 the unit reported zero unconditionally because the
        // per-thread channel tracker did not exist. The idle baseline
        // stays zero — the bug fixed in #2491 is that the *busy* case
        // also reported zero, not that the idle case is wrong.
        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(0);
        report.QueuedMessageCount.ShouldBe(0);
        report.ChannelCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetRuntimeStatusAsync_OneDispatchingChannel_ReportsOneInFlight()
    {
        // #3031: status is derived from per-thread ThreadChannel state,
        // matching AgentActor. A single Dispatching channel with one message
        // is one in-flight thread and zero queued (the head is the in-flight
        // dispatch).
        var threadId = "conv-busy";
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, new List<string> { threadId }));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, new ThreadChannel
            {
                ThreadId = threadId,
                Messages = [CreateMessage(threadId: threadId)],
                Dispatching = true,
            }));

        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(1);
        report.QueuedMessageCount.ShouldBe(0);
        report.ChannelCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetRuntimeStatusAsync_DispatchingChannelWithBacklog_CountsQueuedBeyondHead()
    {
        // A Dispatching channel with three messages contributes one in-flight
        // (the head) and two queued — the unit now has a real FIFO queue ahead
        // of the dispatcher (#3031), unlike the pre-mailbox path that could
        // not queue and reported zero.
        var threadId = "conv-backlog";
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, new List<string> { threadId }));
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, new ThreadChannel
            {
                ThreadId = threadId,
                Messages =
                [
                    CreateMessage(threadId: threadId),
                    CreateMessage(threadId: threadId),
                    CreateMessage(threadId: threadId),
                ],
                Dispatching = true,
            }));

        var report = await _actor.GetRuntimeStatusAsync(TestContext.Current.CancellationToken);

        report.InFlightThreadCount.ShouldBe(1);
        report.QueuedMessageCount.ShouldBe(2);
        report.ChannelCount.ShouldBe(1);
    }

    // --- Per-thread mailbox + drain (#3031, mirrors AgentActor) ---

    [Fact]
    public async Task ReceiveAsync_FirstDomainMessage_CreatesPerThreadChannel()
    {
        // #3031: the first inbound on a thread creates a Dispatching channel
        // and returns immediately — the runtime runs on the background
        // dispatch task, so a busy unit never blocks inbound delivery.
        var threadId = "conv-new";
        var message = CreateMessage(threadId: threadId);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.ThreadId == threadId && c.Dispatching && c.Messages.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SameThreadWhileDispatching_AppendsWithoutSecondDispatcher()
    {
        // While a thread is dispatching, a second inbound on the same thread
        // appends to the channel's FIFO queue — no second dispatcher is
        // launched; the drain loop picks it up when the head completes.
        var threadId = "conv-busy";
        var existing = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, existing));

        var second = CreateMessage(threadId: threadId);
        await _actor.ReceiveAsync(second, TestContext.Current.CancellationToken);

        await _stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.Messages.Count == 2 && c.Dispatching),
            Arg.Any<CancellationToken>());
        _actor.PendingDispatchTask.ShouldBeNull();
    }

    [Fact]
    public async Task OnDispatchExitAsync_MoreQueued_DrainsNextAndKeepsDispatching()
    {
        var threadId = "conv-drain";
        var remaining = CreateMessage(threadId: threadId);
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId), remaining], // head + 1 queued
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        await _actor.OnDispatchExitAsync(threadId, "dispatch completed", TestContext.Current.CancellationToken);

        // Head removed; the queued message becomes the new head; the channel
        // stays Dispatching and a fresh dispatcher is launched for it.
        await _stateManager.Received().SetStateAsync(
            StateKeys.ChannelPrefix + threadId,
            Arg.Is<ThreadChannel>(c => c.Messages.Count == 1 && c.Dispatching),
            Arg.Any<CancellationToken>());
        var dispatch = _actor.PendingDispatchTask.ShouldNotBeNull();
        await dispatch;
        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Arg.Any<Address>(),
            remaining,
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDispatchExitAsync_QueueEmpty_RemovesChannel()
    {
        var threadId = "conv-done";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)], // only the head
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, new List<string> { threadId }));

        await _actor.OnDispatchExitAsync(threadId, "dispatch completed", TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>());
        _actor.PendingDispatchTask.ShouldBeNull();
    }

    [Fact]
    public async Task OnDispatchExitAsync_HaltedWhileDispatching_QuiescesAndDropsBacklog()
    {
        // #2981: an authoritative stop mid-dispatch drops the remaining queue
        // and removes the channel instead of re-arming the drain loop.
        var threadId = "conv-halted";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId), CreateMessage(threadId: threadId)],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, new List<string> { threadId }));
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Stopped));

        await _actor.OnDispatchExitAsync(threadId, "dispatch completed", TestContext.Current.CancellationToken);

        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>());
        _actor.PendingDispatchTask.ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_Cancel_ClearsThreadChannel()
    {
        // #3031: per-thread cancel clears the channel (matches AgentActor) so a
        // subsequent inbound on the same thread starts a fresh drain loop.
        var threadId = "conv-cancel";
        var channel = new ThreadChannel
        {
            ThreadId = threadId,
            Messages = [CreateMessage(threadId: threadId)],
            Dispatching = true,
        };
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(true, new List<string> { threadId }));

        var cancel = CreateMessage(type: MessageType.Cancel, threadId: threadId);
        var result = await _actor.ReceiveAsync(cancel, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull(); // ack
        await _stateManager.Received().TryRemoveStateAsync(
            StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>());
    }

    // --- Control Message Tests ---

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsLifecycleStatusWithMemberCount()
    {
        // ADR-0040 / #2052: members come from the EF graph store, not
        // actor state. Seed two agent edges and verify the status-query
        // payload reflects them.
        _memberGraphStore.SeedAgentMembers(
            TestUnitGuid,
            TestSlugIds.For("agent-1"),
            TestSlugIds.For("agent-2"));

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

    // --- Member Management Tests (ADR-0040 / #2052: EF-backed) ---

    [Fact]
    public async Task AddMemberAsync_NewAgentMember_WritesEdgeToGraphStore()
    {
        var agentId = TestSlugIds.For("new-agent");
        var member = new Address("agent", agentId);

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        var members = await _memberGraphStore.GetMembersAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldContain(member);

        // The actor must NEVER touch the legacy Unit:Members state key.
        await _stateManager.DidNotReceive().SetStateAsync(
            "Unit:Members", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_DoesNotEmitStateChanged()
    {
        var agentId = TestSlugIds.For("existing-agent");
        var member = new Address("agent", agentId);
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, agentId);
        _activityEventBus.ClearReceivedCalls();

        await _actor.AddMemberAsync(member, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged
                && e.Summary.Contains("added")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_RemovesEdgeFromGraphStore()
    {
        var agentId = TestSlugIds.For("agent-to-remove");
        var member = new Address("agent", agentId);
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, agentId);

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        var members = await _memberGraphStore.GetMembersAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldNotContain(member);
    }

    [Fact]
    public async Task RemoveMemberAsync_NonExistentMember_DoesNotEmitStateChanged()
    {
        var member = new Address("agent", TestSlugIds.For("non-existent"));
        _activityEventBus.ClearReceivedCalls();

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged
                && e.Summary.Contains("removed")),
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
    public async Task GetMembersAsync_WithMembers_ReturnsAgentAndSubunitMembers()
    {
        var agentId = TestSlugIds.For("agent-1");
        var subunitId = TestSlugIds.For("sub-unit-1");
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, agentId);
        _memberGraphStore.SeedSubunitChildren(TestUnitGuid, subunitId);

        var result = await _actor.GetMembersAsync(TestContext.Current.CancellationToken);

        result.Length.ShouldBe(2);
        result.ShouldContain(new Address("agent", agentId));
        result.ShouldContain(new Address("unit", subunitId));
    }

    [Fact]
    public async Task GetMembersAsync_AfterFreshActivation_ReadsDirectlyFromEf()
    {
        // ADR-0040 / #2052: simulate an actor restart by seeding the
        // graph store and constructing a fresh actor over the SAME
        // store. The new actor must surface the seeded members on the
        // very first call without any actor-state preload — proving EF
        // is the single source of truth across activations.
        var agentId = TestSlugIds.For("ef-only");
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, agentId);

        var freshActor = new UnitActor(
            ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
            {
                ActorId = new ActorId(TestUnitActorId),
            }),
            _loggerFactory,
            _runtimeInvocationPath,
            _activityEventBus,
            _directoryService,
            _actorProxyFactory,
            new UnitStateCoordinator(_liveConfigStore, Substitute.For<ILogger<UnitStateCoordinator>>()),
            _memberGraphStore,
            humanPermissionStore: _permissionStore);
        SetStateManager(freshActor, Substitute.For<IActorStateManager>());

        var members = await freshActor.GetMembersAsync(TestContext.Current.CancellationToken);
        members.ShouldContain(new Address("agent", agentId));
    }

    // --- Error Handling Tests ---

    [Fact]
    public async Task ReceiveAsync_RuntimePathThrows_IsIsolatedToBackgroundDispatch()
    {
        // #3031: receive is decoupled from dispatch. A runtime path that
        // faults must NOT bubble through ReceiveAsync (which only enqueues
        // into the per-thread mailbox); the fault stays isolated to the
        // background dispatch task. In production the dispatch coordinator
        // handles runtime errors and emits an ErrorOccurred activity (see
        // UnitRuntimeDispatchVisibilityTests) rather than throwing — this test
        // pins that a throw cannot break the actor turn / inbound delivery.
        var message = CreateMessage();
        _runtimeInvocationPath
            .InvokeAsync(
                Arg.Any<Address>(),
                message,
                Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
                Arg.Any<Func<string, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("Runtime path failed"));

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Enqueued and returned without surfacing the dispatch fault.
        result.ShouldBeNull();

        // The fault is observable only on the background dispatch task.
        var dispatch = _actor.PendingDispatchTask.ShouldNotBeNull();
        await Should.ThrowAsync<InvalidOperationException>(async () => await dispatch);
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
    public async Task ReceiveAsync_DomainMessage_EmitsMessageArrivedEvent()
    {
        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.MessageArrived),
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
                e.EventType == ActivityEventType.MessageArrived
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
                e.EventType == ActivityEventType.MessageArrived
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
        var agentId = TestSlugIds.For("agent-to-remove");
        var member = new Address("agent", agentId);
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, agentId);

        await _actor.RemoveMemberAsync(member, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("removed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_SynchronousFailureDuringEnqueue_EmitsErrorOccurredWithStructuredDetails()
    {
        // #3031: runtime-dispatch errors now surface via the dispatch
        // coordinator on the background task (see
        // ReceiveAsync_RuntimePathThrows_IsIsolatedToBackgroundDispatch and the
        // integration UnitRuntimeDispatchVisibilityTests). The UnitActor catch
        // block (#2551) still converts a *synchronous* failure during message
        // handling — e.g. a state read that throws while enqueuing into the
        // per-thread mailbox — into a structured ErrorOccurred
        // { error, agentId, threadId } so the portal activity feed can surface
        // it without raw log access.
        var threadId = Guid.NewGuid().ToString();
        var message = CreateMessage(threadId: threadId);
        const string errorText = "unexpected enqueue failure";

        _stateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(errorText));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ErrorOccurred &&
                e.Severity == ActivitySeverity.Error &&
                e.CorrelationId == threadId &&
                e.Details.HasValue &&
                e.Details.Value.GetProperty("error").GetString() == errorText &&
                e.Details.Value.GetProperty("agentId").GetString() == TestUnitActorId &&
                e.Details.Value.GetProperty("threadId").GetString() == threadId),
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

        status.ShouldBe(LifecycleStatus.Draft);
    }

    [Fact]
    public async Task TransitionAsync_DraftToStopped_SucceedsAndPersists()
    {
        var result = await _actor.TransitionAsync(LifecycleStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
        result.RejectionReason.ShouldBeNull();

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            LifecycleStatus.Stopped,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StoppedToStarting_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Stopped));

        var result = await _actor.TransitionAsync(LifecycleStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Starting);
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            LifecycleStatus.Starting,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StartingToRunning_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Starting));

        var result = await _actor.TransitionAsync(LifecycleStatus.Running, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Running);
    }

    [Fact]
    public async Task TransitionAsync_RunningToStopping_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Running));

        var result = await _actor.TransitionAsync(LifecycleStatus.Stopping, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopping);
    }

    [Fact]
    public async Task TransitionAsync_StoppingToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Stopping));

        var result = await _actor.TransitionAsync(LifecycleStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_ErrorToStopped_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Error));

        var result = await _actor.TransitionAsync(LifecycleStatus.Stopped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
    }

    [Fact]
    public async Task TransitionAsync_StartingToError_Succeeds()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Starting));

        var result = await _actor.TransitionAsync(LifecycleStatus.Error, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Error);
    }

    [Fact]
    public async Task TransitionAsync_RunningToDraft_Rejected()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Running));

        var result = await _actor.TransitionAsync(LifecycleStatus.Draft, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Running);
        result.RejectionReason.ShouldNotBeNull();
        result.RejectionReason.ShouldContain("Running");
        result.RejectionReason.ShouldContain("Draft");

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitLifecycleStatus,
            Arg.Any<LifecycleStatus>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_StoppedToRunning_Rejected()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Stopped));

        var result = await _actor.TransitionAsync(LifecycleStatus.Running, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Stopped);
        result.RejectionReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransitionAsync_Success_EmitsStateChangedEvent()
    {
        await _actor.TransitionAsync(LifecycleStatus.Stopped, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("transitioned")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_Rejected_DoesNotEmitStateChangedEvent()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Running));

        _activityEventBus.ClearReceivedCalls();

        await _actor.TransitionAsync(LifecycleStatus.Draft, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("transitioned")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReportsPersistedStatus()
    {
        _stateManager.TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<LifecycleStatus>(true, LifecycleStatus.Running));

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

    // --- Nested Unit Membership / Cycle Detection Tests (#98 + #2052) ---

    [Fact]
    public async Task AddMemberAsync_UnitMember_NoCycle_PersistsEdge()
    {
        // Sub-unit "team-b" has no unit-members of its own, so adding it is safe.
        var teamBId = TestSlugIds.For("team-b");
        var subAddress = new Address("unit", teamBId);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        var children = await _memberGraphStore.ListDirectSubunitChildrenAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldContain(teamBId);
    }

    [Fact]
    public async Task AddMemberAsync_SelfLoop_ByGuidIdentity_Throws()
    {
        // The actor's own Address is unit://{actorId}.
        var selfAddress = new Address("unit", TestUnitGuid);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(selfAddress, TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(selfAddress);
        ex.ParentUnit.ShouldBe(selfAddress);
        ex.CyclePath.ShouldNotBeEmpty();

        var children = await _memberGraphStore.ListDirectSubunitChildrenAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddMemberAsync_TwoCycle_Throws()
    {
        // B already contains A. Adding B to A must be rejected
        // because the resulting graph would close A -> B -> A.
        var teamBId = TestSlugIds.For("team-b");
        _memberGraphStore.SeedSubunitChildren(teamBId, TestUnitGuid);

        var bAddress = new Address("unit", teamBId);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(bAddress, TestContext.Current.CancellationToken));

        ex.CandidateMember.ShouldBe(bAddress);
        ex.Message.ShouldContain("cycle");
        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(2);

        var children = await _memberGraphStore.ListDirectSubunitChildrenAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddMemberAsync_DeepCycle_Throws()
    {
        // C -> B -> A. Adding C to A must be rejected.
        var teamCId = TestSlugIds.For("team-c");
        var teamBId = TestSlugIds.For("team-b");
        _memberGraphStore.SeedSubunitChildren(teamCId, teamBId);
        _memberGraphStore.SeedSubunitChildren(teamBId, TestUnitGuid);

        var cAddress = new Address("unit", teamCId);

        var ex = await Should.ThrowAsync<CyclicMembershipException>(() =>
            _actor.AddMemberAsync(cAddress, TestContext.Current.CancellationToken));

        ex.CyclePath.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task AddMemberAsync_AgentMember_SkipsCycleDetection()
    {
        // Agent members are leaves and cannot introduce cycles. The
        // graph store's sub-unit walk must not be queried for agent-
        // typed adds.
        var agentAddress = new Address("agent", TestSlugIds.For("agent-leaf"));

        await _actor.AddMemberAsync(agentAddress, TestContext.Current.CancellationToken);

        var members = await _memberGraphStore.GetMembersAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        members.ShouldContain(agentAddress);
    }

    [Fact]
    public async Task AddMemberAsync_BenignSubGraphCycle_DoesNotFalsePositive()
    {
        // X -> Y -> X (benign 2-cycle in the subgraph, not involving the test unit).
        var teamXId = TestSlugIds.For("team-x");
        var teamYId = TestSlugIds.For("team-y");
        _memberGraphStore.SeedSubunitChildren(teamXId, teamYId);
        _memberGraphStore.SeedSubunitChildren(teamYId, teamXId);

        var subAddress = new Address("unit", teamXId);

        await _actor.AddMemberAsync(subAddress, TestContext.Current.CancellationToken);

        var children = await _memberGraphStore.ListDirectSubunitChildrenAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldContain(teamXId);
    }

    [Fact]
    public async Task RemoveMemberAsync_UnitMember_RemovesEdge()
    {
        var teamBId = TestSlugIds.For("team-b");
        _memberGraphStore.SeedSubunitChildren(TestUnitGuid, teamBId);
        var subAddress = new Address("unit", teamBId);

        await _actor.RemoveMemberAsync(subAddress, TestContext.Current.CancellationToken);

        var children = await _memberGraphStore.ListDirectSubunitChildrenAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        children.ShouldNotContain(teamBId);
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_WithMixedAgentAndUnitMembers_InvokesRuntimePath()
    {
        // Mixed members live in the EF graph store; domain dispatch
        // routes through the runtime invocation path regardless.
        _memberGraphStore.SeedAgentMembers(TestUnitGuid, TestSlugIds.For("agent-1"));
        _memberGraphStore.SeedSubunitChildren(TestUnitGuid, TestSlugIds.For("team-b"));

        var message = CreateMessage();

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestUnitActorId),
            message,
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
    }

    // #939 — Draft → Starting is rejected; units must pass through Validating first

    [Fact]
    public async Task TransitionAsync_DraftToStarting_IsRejected()
    {
        // Draft → Starting is no longer a valid transition (#939).
        // Units must go Draft → Validating → Stopped → Starting.
        var result = await _actor.TransitionAsync(LifecycleStatus.Starting, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.CurrentStatus.ShouldBe(LifecycleStatus.Draft);
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
