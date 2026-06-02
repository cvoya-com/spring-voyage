// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Initiative;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentMailboxCoordinator"/> validating the
/// pre-validation guards relocated from <c>AgentActor.HandleDomainMessageAsync</c>
/// (#1349) and the per-thread channel routing
/// (#2076 / ADR-0030 §3 §44).
/// </summary>
public class AgentMailboxCoordinatorTests
{
    private static readonly Guid AgentGuid = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly string AgentId = AgentGuid.ToString("N");
    private static readonly Guid SenderGuid = new("aaaaaaaa-1111-1111-1111-000000000002");
    private const string ThreadId = "thread-001";

    private readonly AgentMailboxCoordinator _coordinator;
    private readonly List<ActivityEvent> _emittedEvents = [];

    public AgentMailboxCoordinatorTests()
    {
        var logger = Substitute.For<ILogger<AgentMailboxCoordinator>>();
        _coordinator = new AgentMailboxCoordinator(logger);
    }

    // --- Guard 0: Membership-disabled (#1349) ---

    [Fact]
    public async Task HandleDomainMessageAsync_MembershipDisabled_RejectsWithDecisionMadeEvent()
    {
        var message = CreateMessage();
        var disabledMetadata = new AgentMetadata(Enabled: false);
        var getChannelCalled = false;
        var dispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: disabledMetadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, ct) =>
                Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getChannel: (_, _) =>
            {
                getChannelCalled = true;
                return Task.FromResult<ThreadChannel?>(null);
            },
            saveChannel: (_, _) => Task.CompletedTask,
            dispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (evt, _) =>
            {
                _emittedEvents.Add(evt);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        getChannelCalled.ShouldBeFalse(
            "Guard 0 must short-circuit before reading actor state when membership is disabled.");
        dispatchCalled.ShouldBeFalse(
            "Guard 0 must not dispatch when membership is disabled.");
        _emittedEvents.ShouldHaveSingleItem();
        _emittedEvents[0].EventType.ShouldBe(ActivityEventType.DecisionMade);
        _emittedEvents[0].Summary.ShouldContain("membership disabled");
    }

    // --- Guard 1: Unit-policy check (#1349) ---

    [Fact]
    public async Task HandleDomainMessageAsync_PolicyDenied_RejectsWithDecisionMadeEvent()
    {
        var message = CreateMessage();
        var enabledMetadata = new AgentMetadata(Enabled: true);
        var verdict = new PolicyVerdict(
            Dimension: "model",
            DecisionTag: "BlockedByUnitModelPolicy",
            Summary: "Model gpt-4 is not permitted by unit policy.",
            Decision: PolicyDecision.Deny("model denied", "unit-1"));

        var dispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: enabledMetadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, ct) =>
                Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, verdict)),
            getChannel: (_, _) => Task.FromResult<ThreadChannel?>(null),
            saveChannel: (_, _) => Task.CompletedTask,
            dispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (evt, _) =>
            {
                _emittedEvents.Add(evt);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        dispatchCalled.ShouldBeFalse(
            "Guard 1 must not dispatch when a PolicyVerdict is returned.");
        _emittedEvents.ShouldHaveSingleItem();
        _emittedEvents[0].EventType.ShouldBe(ActivityEventType.DecisionMade);
        _emittedEvents[0].Summary.ShouldContain("Model gpt-4 is not permitted");
    }

    // --- Routing cases (guard-pass path) ---

    [Fact]
    public async Task HandleDomainMessageAsync_NoChannel_CreatesAndDispatches()
    {
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        ThreadChannel? saved = null;
        ThreadChannel? dispatched = null;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getChannel: (_, _) => Task.FromResult<ThreadChannel?>(null),
            saveChannel: (ch, _) =>
            {
                saved = ch;
                return Task.CompletedTask;
            },
            dispatch: (ch, _, _) =>
            {
                dispatched = ch;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        saved.ShouldNotBeNull();
        saved!.ThreadId.ShouldBe(ThreadId);
        saved.Dispatching.ShouldBeTrue();
        dispatched.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleDomainMessageAsync_ChannelDispatching_AppendsWithoutDispatching()
    {
        var existing = new ThreadChannel
        {
            ThreadId = ThreadId,
            Messages = [CreateMessage()],
            Dispatching = true,
        };
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        ThreadChannel? saved = null;
        var dispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getChannel: (_, _) => Task.FromResult<ThreadChannel?>(existing),
            saveChannel: (ch, _) =>
            {
                saved = ch;
                return Task.CompletedTask;
            },
            dispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        dispatchCalled.ShouldBeFalse(
            "Channel mid-drain must not spawn a parallel dispatcher; per-thread FIFO requires the existing drain to pick up the appended message.");
        saved.ShouldNotBeNull();
        saved!.Messages.Count.ShouldBe(2);
        saved.Dispatching.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleDomainMessageAsync_ChannelIdle_AppendsAndRestartsDispatch()
    {
        // A channel that was kept around between dispatches (Dispatching=false)
        // must restart its dispatch loop on the next inbound on the same
        // thread.
        var existing = new ThreadChannel
        {
            ThreadId = ThreadId,
            Messages = [],
            Dispatching = false,
        };
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        ThreadChannel? saved = null;
        var dispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getChannel: (_, _) => Task.FromResult<ThreadChannel?>(existing),
            saveChannel: (ch, _) =>
            {
                saved = ch;
                return Task.CompletedTask;
            },
            dispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        dispatchCalled.ShouldBeTrue();
        saved.ShouldNotBeNull();
        saved!.Dispatching.ShouldBeTrue();
        saved.Messages.Count.ShouldBe(1);
    }

    // --- Lifecycle gate (#2981 / subsumed #2978) ---

    [Theory]
    [InlineData(LifecycleStatus.Stopped)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Error)]
    public async Task HandleDomainMessageAsync_Halted_DropsWithDecisionMadeEvent(LifecycleStatus status)
    {
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        var getChannelCalled = false;
        var dispatchCalled = false;
        var policiesApplied = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            lifecycleStatus: status,
            applyUnitPolicies: (eff, _) =>
            {
                policiesApplied = true;
                return Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null));
            },
            getChannel: (_, _) =>
            {
                getChannelCalled = true;
                return Task.FromResult<ThreadChannel?>(null);
            },
            saveChannel: (_, _) => Task.CompletedTask,
            dispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (evt, _) =>
            {
                _emittedEvents.Add(evt);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // The lifecycle gate runs first: no channel read, no policy
        // evaluation, no dispatch — the message is dropped.
        getChannelCalled.ShouldBeFalse("A halted agent must not read or create a channel.");
        policiesApplied.ShouldBeFalse("A halted agent must short-circuit before policy evaluation.");
        dispatchCalled.ShouldBeFalse("A halted agent must not dispatch.");
        _emittedEvents.ShouldHaveSingleItem();
        _emittedEvents[0].EventType.ShouldBe(ActivityEventType.DecisionMade);
        _emittedEvents[0].Summary.ShouldContain(status.ToString());
    }

    [Fact]
    public async Task HandleDomainMessageAsync_Running_PassesLifecycleGate()
    {
        // A non-halted status must not block: the message routes normally.
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        var dispatched = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            lifecycleStatus: LifecycleStatus.Running,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getChannel: (_, _) => Task.FromResult<ThreadChannel?>(null),
            saveChannel: (_, _) => Task.CompletedTask,
            dispatch: (_, _, _) =>
            {
                dispatched = true;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        dispatched.ShouldBeTrue("A running agent must route the message through to dispatch.");
    }

    // --- Helpers ---

    private static Message CreateMessage(string? threadId = null) =>
        new(
            Guid.NewGuid(),
            new Address("agent", SenderGuid),
            new Address("agent", AgentGuid),
            MessageType.Domain,
            threadId ?? ThreadId,
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
}
