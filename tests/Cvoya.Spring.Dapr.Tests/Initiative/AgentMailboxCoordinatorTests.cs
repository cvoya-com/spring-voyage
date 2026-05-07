// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
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
/// (#1349) and the three routing cases.
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

    /// <summary>
    /// Guard 0 relocated from AgentActor (#1349): when effective.Enabled is
    /// false the coordinator must short-circuit, emit a DecisionMade
    /// "MembershipDisabled" event, and NOT call getActiveConversation or
    /// activateAndDispatch.
    /// </summary>
    [Fact]
    public async Task HandleDomainMessageAsync_MembershipDisabled_RejectsWithDecisionMadeEvent()
    {
        var message = CreateMessage();
        var disabledMetadata = new AgentMetadata(Enabled: false);
        var getActiveConversationCalled = false;
        var activateAndDispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: disabledMetadata,
            applyUnitPolicies: (eff, ct) =>
                Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getActiveConversation: ct =>
            {
                getActiveConversationCalled = true;
                return Task.FromResult<ThreadChannel?>(null);
            },
            setActiveConversation: (_, _) => Task.CompletedTask,
            getPendingList: _ => Task.FromResult<List<ThreadChannel>?>(null),
            setPendingList: (_, _) => Task.CompletedTask,
            activateAndDispatch: (_, _, _) =>
            {
                activateAndDispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (evt, _) =>
            {
                _emittedEvents.Add(evt);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        getActiveConversationCalled.ShouldBeFalse(
            "Guard 0 must short-circuit before reading actor state when membership is disabled.");
        activateAndDispatchCalled.ShouldBeFalse(
            "Guard 0 must not dispatch when membership is disabled.");
        _emittedEvents.ShouldHaveSingleItem();
        _emittedEvents[0].EventType.ShouldBe(ActivityEventType.DecisionMade);
        _emittedEvents[0].Summary.ShouldContain("membership disabled");
    }

    // --- Guard 1: Unit-policy check (#1349) ---

    /// <summary>
    /// Guard 1 relocated from AgentActor (#1349): when applyUnitPolicies returns
    /// a non-null PolicyVerdict the coordinator must short-circuit, emit a
    /// DecisionMade "BlockedByUnitPolicy" event, and NOT call activateAndDispatch.
    /// </summary>
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

        var activateAndDispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: enabledMetadata,
            applyUnitPolicies: (eff, ct) =>
                Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, verdict)),
            getActiveConversation: _ => Task.FromResult<ThreadChannel?>(null),
            setActiveConversation: (_, _) => Task.CompletedTask,
            getPendingList: _ => Task.FromResult<List<ThreadChannel>?>(null),
            setPendingList: (_, _) => Task.CompletedTask,
            activateAndDispatch: (_, _, _) =>
            {
                activateAndDispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (evt, _) =>
            {
                _emittedEvents.Add(evt);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        activateAndDispatchCalled.ShouldBeFalse(
            "Guard 1 must not dispatch when a PolicyVerdict is returned.");
        _emittedEvents.ShouldHaveSingleItem();
        _emittedEvents[0].EventType.ShouldBe(ActivityEventType.DecisionMade);
        _emittedEvents[0].Summary.ShouldContain("Model gpt-4 is not permitted");
    }

    // --- Routing cases (guard-pass path) ---

    /// <summary>
    /// Case 1: when no thread is active the message creates a new active
    /// thread and calls activateAndDispatch.
    /// </summary>
    [Fact]
    public async Task HandleDomainMessageAsync_NoActiveThread_ActivatesAndDispatches()
    {
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        ThreadChannel? activatedChannel = null;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getActiveConversation: _ => Task.FromResult<ThreadChannel?>(null),
            setActiveConversation: (ch, _) =>
            {
                activatedChannel = ch;
                return Task.CompletedTask;
            },
            getPendingList: _ => Task.FromResult<List<ThreadChannel>?>(null),
            setPendingList: (_, _) => Task.CompletedTask,
            activateAndDispatch: (ch, _, _) =>
            {
                activatedChannel ??= ch;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        activatedChannel.ShouldNotBeNull();
        activatedChannel!.ThreadId.ShouldBe(ThreadId);
    }

    /// <summary>
    /// Case 2: a message for the already-active thread appends to the channel
    /// without dispatching a new task.
    /// </summary>
    [Fact]
    public async Task HandleDomainMessageAsync_SameActiveThread_AppendsMessage()
    {
        var existing = new ThreadChannel { ThreadId = ThreadId, Messages = [CreateMessage()] };
        var message = CreateMessage();
        var metadata = new AgentMetadata(Enabled: true);
        ThreadChannel? updated = null;
        var dispatchCalled = false;

        await _coordinator.HandleDomainMessageAsync(
            agentId: AgentId,
            message: message,
            effective: metadata,
            applyUnitPolicies: (eff, _) => Task.FromResult<(AgentMetadata, PolicyVerdict?)>((eff, null)),
            getActiveConversation: _ => Task.FromResult<ThreadChannel?>(existing),
            setActiveConversation: (ch, _) =>
            {
                updated = ch;
                return Task.CompletedTask;
            },
            getPendingList: _ => Task.FromResult<List<ThreadChannel>?>(null),
            setPendingList: (_, _) => Task.CompletedTask,
            activateAndDispatch: (_, _, _) =>
            {
                dispatchCalled = true;
                return Task.CompletedTask;
            },
            emitActivity: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        dispatchCalled.ShouldBeFalse();
        updated.ShouldNotBeNull();
        updated!.Messages.Count.ShouldBe(2);
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