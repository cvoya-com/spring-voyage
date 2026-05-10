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
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the receive-path per-membership config override
/// introduced by <c>#243</c>. Verifies that <see cref="AgentActor"/>
/// merges its own global metadata with any <see cref="UnitMembership"/>
/// row on the <c>(sender-unit, agent)</c> edge, and that the resulting
/// effective metadata drives dispatch decisions for the turn.
/// </summary>
/// <remarks>
/// Per ADR-0040 / #2048 the agent-global metadata lives on the
/// <c>agent_live_config</c> EF row, so this test seeds that store via
/// <see cref="InMemoryAgentLiveConfigStore"/> rather than priming
/// actor-state delegates as before.
/// </remarks>
public class AgentActorEffectiveMetadataTests
{
    // Stable UUIDs for the agent actor and test units.
    private static readonly Guid AgentActorUuid = new("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid UnitAUuid = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UnitBUuid = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly string AgentId = AgentActorUuid.ToString("N");

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly InMemoryAgentLiveConfigStore _liveConfigStore = new();
    private readonly AgentActor _actor;

    public AgentActorEffectiveMetadataTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        _definitionProvider.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(AgentId, "Test", "Agent instructions", null));

        // Wire directory service to resolve unit addresses → directory entries.
        _directoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == UnitAUuid), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(new Address("unit", UnitAUuid), UnitAUuid, "unit-a", string.Empty, null, DateTimeOffset.UtcNow));
        _directoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == UnitBUuid), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(new Address("unit", UnitBUuid), UnitBUuid, "unit-b", string.Empty, null, DateTimeOffset.UtcNow));
        // Unknown units → null by default (NSubstitute returns null for unmatched reference-type calls).

        // The actor ID is the agent's stable UUID (not the slug); pass it
        // explicitly so AgentActor.ResolveEffectiveMetadataAsync can parse
        // Id.GetId() into the agent Guid.
        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentId),
        });

        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>().WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(_liveConfigStore, Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            directoryService: _directoryService);

        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));

        // Default: no membership row for any (unit, agent) pair.
        _membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);
    }

    private static Message DomainMessageFrom(Address from, string threadId = "conv-1")
    {
        return new Message(
            Guid.NewGuid(),
            from,
            new Address("agent", AgentActorUuid),
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement(new { task = "do-it" }),
            DateTimeOffset.UtcNow);
    }

    private void SetAgentGlobalMetadata(
        string? model = null,
        string? specialty = null,
        bool? enabled = null,
        AgentExecutionMode? executionMode = null)
    {
        _liveConfigStore.SeedMetadata(AgentActorUuid, new AgentMetadata(
            Model: model,
            Specialty: specialty,
            Enabled: enabled,
            ExecutionMode: executionMode));
    }

    [Fact]
    public async Task UnitSender_ModelOverride_DrivesEffectiveModelForTurn()
    {
        SetAgentGlobalMetadata(model: "claude-3-haiku");

        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitAUuid, AgentActorUuid, Model: "gpt-4", Enabled: true));

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

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
        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitAUuid, AgentActorUuid, Enabled: false));

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

        var ack = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Still acks so the caller's message pipeline unblocks, but no dispatch is performed.
        ack.ShouldNotBeNull();
        _actor.PendingDispatchTask.ShouldBeNull();

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext?>(),
            Arg.Any<CancellationToken>());

        // No per-thread channel was written either — the agent remained idle.
        await _stateManager.DidNotReceive().SetStateAsync(
            Arg.Is<string>(k => k.StartsWith(StateKeys.ChannelPrefix)),
            Arg.Any<ThreadChannel>(),
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
        SetAgentGlobalMetadata(specialty: "generalist");

        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitAUuid, AgentActorUuid, Specialty: "reviewer", Enabled: true));

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

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
        SetAgentGlobalMetadata(executionMode: AgentExecutionMode.Auto);

        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(
                UnitAUuid,
                AgentActorUuid,
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand));

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

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
        SetAgentGlobalMetadata(model: "claude-3-opus", specialty: "generalist");

        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

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

        var message = DomainMessageFrom(Address.For("webhook", TestSlugIds.HexFor("github/incoming")));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _membershipRepository.DidNotReceive().GetAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
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

        var message = DomainMessageFrom(Address.For("agent", TestSlugIds.HexFor("peer-agent")));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _membershipRepository.DidNotReceive().GetAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerMembershipOverride_IgnoredForSubsequentMessagesInSameConversation()
    {
        // The dispatch task is only kicked off when a conversation becomes
        // active; subsequent messages that append to the active conversation
        // don't redo the merge or dispatch. This documents the current
        // boundary: effective metadata is resolved at conversation-start time.
        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitAUuid, AgentActorUuid, Model: "gpt-4", Enabled: true));

        var msg1 = DomainMessageFrom(new Address("unit", UnitAUuid), "conv-1");
        await _actor.ReceiveAsync(msg1, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // After the first message a per-thread channel exists for conv-1
        // and is mid-drain. Per #2076 the second message appends without
        // launching a parallel dispatcher.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + "conv-1", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true,
                new ThreadChannel { ThreadId = "conv-1", Messages = [msg1], Dispatching = true }));

        var msg2 = DomainMessageFrom(new Address("unit", UnitAUuid), "conv-1");
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
        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitAUuid, AgentActorUuid, Model: "gpt-4", Enabled: true));
        _membershipRepository.GetAsync(UnitBUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitBUuid, AgentActorUuid, Model: "claude-3-5-sonnet", Enabled: true));

        // Turn 1: unit-a opens conversation conv-a; verify gpt-4.
        var msgA = DomainMessageFrom(new Address("unit", UnitAUuid), "conv-a");
        await _actor.ReceiveAsync(msgA, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == msgA.Id),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null && ctx.EffectiveMetadata != null && ctx.EffectiveMetadata.Model == "gpt-4"),
            Arg.Any<CancellationToken>());

        // Turn 2: unit-b sends conv-b. Per #2076 / ADR-0030 §44 conv-b
        // creates an independent per-thread channel — it does NOT queue
        // behind conv-a. The membership merge runs against unit-b
        // because the inbound's From address is unit-b.
        var msgB = DomainMessageFrom(new Address("unit", UnitBUuid), "conv-b");
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
        SetAgentGlobalMetadata(model: "claude-3-opus");

        _membershipRepository.GetAsync(UnitAUuid, AgentActorUuid, Arg.Any<CancellationToken>())
            .Returns<Task<UnitMembership?>>(_ => throw new InvalidOperationException("db down"));

        var message = DomainMessageFrom(new Address("unit", UnitAUuid));

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
