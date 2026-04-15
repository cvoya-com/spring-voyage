// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the receive-path per-membership config override introduced by
/// <c>#243</c>. Verifies that <see cref="AgentActor"/> merges its own global
/// metadata with any <see cref="UnitMembership"/> row on the
/// <c>(sender-unit, agent)</c> edge, and that the resulting effective metadata
/// drives dispatch decisions for the turn.
/// </summary>
public class AgentActorEffectiveMetadataTests
{
    private const string AgentId = "test-agent";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly AgentActor _actor;

    public AgentActorEffectiveMetadataTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        _definitionProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(AgentId, "Test", "Agent instructions", null));

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentId),
        });

        var reflectionRegistry = Substitute.For<IReflectionActionHandlerRegistry>();
        reflectionRegistry.Find(Arg.Any<string?>()).Returns((IReflectionActionHandler?)null);
        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>().WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IInitiativeEngine>(),
            Substitute.For<IAgentPolicyStore>(),
            _dispatcher,
            _router,
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            reflectionRegistry,
            unitPolicyEnforcer,
            loggerFactory);

        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(false, default!));
        _stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(false, default!));

        // Default: no agent-global metadata set.
        _stateManager.TryGetStateAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
        _stateManager.TryGetStateAsync<bool>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<bool>(false, default));
        _stateManager.TryGetStateAsync<AgentExecutionMode>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<AgentExecutionMode>(false, default));

        // Default: no membership row for any (unit, agent) pair.
        _membershipRepository
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);
    }

    private static Message DomainMessageFrom(Address from, string conversationId = "conv-1")
    {
        return new Message(
            Guid.NewGuid(),
            from,
            new Address("agent", AgentId),
            MessageType.Domain,
            conversationId,
            JsonSerializer.SerializeToElement(new { task = "do-it" }),
            DateTimeOffset.UtcNow);
    }

    private void SetAgentGlobalMetadata(
        string? model = null,
        string? specialty = null,
        bool? enabled = null,
        AgentExecutionMode? executionMode = null)
    {
        if (model is not null)
        {
            _stateManager.TryGetStateAsync<string>(StateKeys.AgentModel, Arg.Any<CancellationToken>())
                .Returns(new ConditionalValue<string>(true, model));
        }

        if (specialty is not null)
        {
            _stateManager.TryGetStateAsync<string>(StateKeys.AgentSpecialty, Arg.Any<CancellationToken>())
                .Returns(new ConditionalValue<string>(true, specialty));
        }

        if (enabled is not null)
        {
            _stateManager.TryGetStateAsync<bool>(StateKeys.AgentEnabled, Arg.Any<CancellationToken>())
                .Returns(new ConditionalValue<bool>(true, enabled.Value));
        }

        if (executionMode is not null)
        {
            _stateManager.TryGetStateAsync<AgentExecutionMode>(StateKeys.AgentExecutionMode, Arg.Any<CancellationToken>())
                .Returns(new ConditionalValue<AgentExecutionMode>(true, executionMode.Value));
        }
    }

    [Fact]
    public async Task UnitSender_ModelOverride_DrivesEffectiveModelForTurn()
    {
        const string UnitId = "unit-a";
        SetAgentGlobalMetadata(model: "claude-3-haiku");

        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId, Model: "gpt-4", Enabled: true));

        var message = DomainMessageFrom(new Address("unit", UnitId));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == message.Id),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.Model == "gpt-4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnitSender_MembershipDisabled_ShortCircuitsWithoutDispatch()
    {
        const string UnitId = "unit-a";
        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId, Enabled: false));

        var message = DomainMessageFrom(new Address("unit", UnitId));

        var ack = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Still acks so the caller's message pipeline unblocks, but no dispatch is performed.
        ack.ShouldNotBeNull();
        _actor.PendingDispatchTask.ShouldBeNull();

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext?>(),
            Arg.Any<CancellationToken>());

        // No active conversation was written either — the agent remained idle.
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.ActiveConversation,
            Arg.Any<ConversationChannel>(),
            Arg.Any<CancellationToken>());

        // Operators need to be able to see this as a DecisionMade with a
        // membership-disabled reason — otherwise silent skips are invisible.
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("membership disabled")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnitSender_SpecialtyOverride_PropagatesInEffectiveMetadata()
    {
        const string UnitId = "unit-a";
        SetAgentGlobalMetadata(specialty: "generalist");

        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId, Specialty: "reviewer", Enabled: true));

        var message = DomainMessageFrom(new Address("unit", UnitId));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.Specialty == "reviewer"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnitSender_ExecutionModeOverride_PropagatesInEffectiveMetadata()
    {
        const string UnitId = "unit-a";
        SetAgentGlobalMetadata(executionMode: AgentExecutionMode.Auto);

        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(
                UnitId,
                AgentId,
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand));

        var message = DomainMessageFrom(new Address("unit", UnitId));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.ExecutionMode == AgentExecutionMode.OnDemand),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnitSender_NoMembershipRow_FallsBackToAgentGlobal()
    {
        // Defensive — post-C2b-1 the backfill service creates a membership for
        // every agent that had a ParentUnit, but the receive path must still
        // tolerate a missing row without exploding.
        const string UnitId = "unit-a";
        SetAgentGlobalMetadata(model: "claude-3-opus", specialty: "generalist");

        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var message = DomainMessageFrom(new Address("unit", UnitId));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.Model == "claude-3-opus" &&
                ctx.EffectiveMetadata.Specialty == "generalist"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonUnitSender_DoesNotLookUpMembership()
    {
        SetAgentGlobalMetadata(model: "claude-3-haiku");

        var message = DomainMessageFrom(new Address("webhook", "github/incoming"));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _membershipRepository.DidNotReceive().GetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.Model == "claude-3-haiku"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentSender_DoesNotLookUpMembership()
    {
        SetAgentGlobalMetadata(model: "claude-3-haiku");

        var message = DomainMessageFrom(new Address("agent", "peer-agent"));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _membershipRepository.DidNotReceive().GetAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerMembershipOverride_IgnoredForSubsequentMessagesInSameConversation()
    {
        // The dispatch task is only kicked off when a conversation becomes
        // active; subsequent messages that append to the active conversation
        // don't redo the merge or dispatch. This documents the current
        // boundary: effective metadata is resolved at conversation-start time.
        const string UnitId = "unit-a";
        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitId, AgentId, Model: "gpt-4", Enabled: true));

        var msg1 = DomainMessageFrom(new Address("unit", UnitId), "conv-1");
        await _actor.ReceiveAsync(msg1, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // After the first message the active conversation exists.
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(true,
                new ConversationChannel { ConversationId = "conv-1", Messages = [msg1] }));

        var msg2 = DomainMessageFrom(new Address("unit", UnitId), "conv-1");
        await _actor.ReceiveAsync(msg2, TestContext.Current.CancellationToken);

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoUnitsWithDifferentOverrides_ReceiveSequentially_EachSeesOwnConfig()
    {
        // unit-a → model gpt-4; unit-b → model sonnet.
        _membershipRepository.GetAsync("unit-a", AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership("unit-a", AgentId, Model: "gpt-4", Enabled: true));
        _membershipRepository.GetAsync("unit-b", AgentId, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership("unit-b", AgentId, Model: "claude-3-5-sonnet", Enabled: true));

        // Turn 1: unit-a opens conversation conv-a; verify gpt-4.
        var msgA = DomainMessageFrom(new Address("unit", "unit-a"), "conv-a");
        await _actor.ReceiveAsync(msgA, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == msgA.Id),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null && ctx.EffectiveMetadata != null && ctx.EffectiveMetadata.Model == "gpt-4"),
            Arg.Any<CancellationToken>());

        // Turn 2: unit-b sends conv-b. Because conv-a is active, conv-b
        // goes to pending — but when we promote conv-b, the membership
        // merge must run against unit-b. Simulate the flow: cancel conv-a,
        // then send conv-b fresh (no active).
        _stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(false, default!));

        var msgB = DomainMessageFrom(new Address("unit", "unit-b"), "conv-b");
        await _actor.ReceiveAsync(msgB, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == msgB.Id),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null && ctx.EffectiveMetadata != null && ctx.EffectiveMetadata.Model == "claude-3-5-sonnet"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MembershipLookupThrows_FallsBackToAgentGlobal()
    {
        const string UnitId = "unit-a";
        SetAgentGlobalMetadata(model: "claude-3-opus");

        _membershipRepository.GetAsync(UnitId, AgentId, Arg.Any<CancellationToken>())
            .Returns<Task<UnitMembership?>>(_ => throw new InvalidOperationException("db down"));

        var message = DomainMessageFrom(new Address("unit", UnitId));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // Dispatch still happens — membership lookup failure must not block
        // normal message handling. The fallback is the agent's global config.
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.EffectiveMetadata != null &&
                ctx.EffectiveMetadata.Model == "claude-3-opus"),
            Arg.Any<CancellationToken>());
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField(
            "<StateManager>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

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
}