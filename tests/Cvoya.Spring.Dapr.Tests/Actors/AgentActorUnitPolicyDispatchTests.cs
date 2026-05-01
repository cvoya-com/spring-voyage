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
/// Tests covering the wave-7 C6 dispatch-path policy gates: model caps
/// (#247), cost caps (#248), and execution-mode policy (#249) applied
/// inside <see cref="AgentActor"/> right after per-membership metadata is
/// resolved. Initiative-action policy (#250) is exercised via
/// <see cref="AgentActorReflectionDispatchTests"/> (the reflection-dispatch
/// path is the natural surface for it).
/// </summary>
public class AgentActorUnitPolicyDispatchTests
{
    private const string AgentId = "ada";
    private const string UnitId = "engineering";

    // Stable UUIDs for membership mock lookups (post #1492 interface).
    private static readonly Guid AgentAdaUuid = new("aadaadaa-0000-0000-0000-000000000001");
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitPolicyEnforcer _enforcer = Substitute.For<IUnitPolicyEnforcer>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly AgentActor _actor;

    public AgentActorUnitPolicyDispatchTests()
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

        // Actor ID is the stable UUID; definition lookup keyed by UUID string.
        _definitionProvider.GetByIdAsync(AgentAdaUuid.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(AgentAdaUuid.ToString(), "Test", "instructions", null));

        // Wire directory service: unit slug → UUID entry.
        _directoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitId),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", UnitId),
                UnitEngineeringUuid.ToString(),
                UnitId,
                string.Empty,
                null,
                DateTimeOffset.UtcNow));

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentAdaUuid.ToString()),
        });

        _membershipRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        _enforcer.WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            Array.Empty<ISkillRegistry>(),
            _membershipRepository,
            _enforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            directoryService: _directoryService);

        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));
        _stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(false, default!));
        _stateManager.TryGetStateAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
        _stateManager.TryGetStateAsync<bool>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<bool>(false, default));
        _stateManager.TryGetStateAsync<AgentExecutionMode>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<AgentExecutionMode>(false, default));
    }

    // -----------------------------------------------------------------
    // Model caps (#247)
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeniedModel_RefusesTurn_EmitsBlockedEvent_NoDispatch()
    {
        ArrangeMembership(model: "gpt-4");

        _enforcer.EvaluateModelAsync(Arg.Any<string>(), "gpt-4", Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("Model 'gpt-4' is blocked.", UnitId));

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("Model 'gpt-4' is blocked")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowedModel_TurnDispatchesNormally()
    {
        ArrangeMembership(model: "gpt-4");

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnitModelPolicy_OverridesPerMembershipModel()
    {
        // Per-membership Model = "gpt-4" — but the unit blocks "gpt-4" so the
        // turn must be refused. Demonstrates "unit policy wins over membership".
        ArrangeMembership(model: "gpt-4");

        _enforcer.EvaluateModelAsync(Arg.Any<string>(), "gpt-4", Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("Model 'gpt-4' is blocked.", UnitId));

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoModelSelected_SkipsModelEvaluation_DispatchProceeds()
    {
        // No model on either agent or membership — nothing to enforce.
        ArrangeMembership();

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _enforcer.DidNotReceive().EvaluateModelAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------
    // Cost caps (#248)
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeniedCost_RefusesTurn_EmitsBlockedEvent()
    {
        ArrangeMembership();

        _enforcer.EvaluateCostAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Deny("Hourly spend exceeds cap.", UnitId));

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.DecisionMade &&
                e.Summary.Contains("Hourly spend")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnforcerCostThrows_AllowsTurnInsteadOfLosingIt()
    {
        ArrangeMembership();

        _enforcer.EvaluateCostAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns<Task<PolicyDecision>>(_ => throw new InvalidOperationException("simulated outage"));

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------
    // Execution mode (#249)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ForcedExecutionMode_CoercesEffectiveMetadataForTurn()
    {
        ArrangeMembership(executionMode: AgentExecutionMode.Auto);

        _enforcer.ResolveExecutionModeAsync(Arg.Any<string>(), Arg.Any<AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionModeResolution(PolicyDecision.Allowed, AgentExecutionMode.OnDemand));

        var message = DomainMessageFromUnit();
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
    public async Task DeniedExecutionMode_RefusesTurn()
    {
        ArrangeMembership(executionMode: AgentExecutionMode.Auto);

        _enforcer.ResolveExecutionModeAsync(Arg.Any<string>(), Arg.Any<AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionModeResolution(
                PolicyDecision.Deny("Mode 'Auto' not in unit allow-list.", UnitId),
                AgentExecutionMode.Auto));

        var message = DomainMessageFromUnit();
        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private void ArrangeMembership(string? model = null, AgentExecutionMode? executionMode = null)
    {
        _membershipRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(
                UnitEngineeringUuid, AgentAdaUuid,
                Model: model,
                Enabled: true,
                ExecutionMode: executionMode));
    }

    private static Message DomainMessageFromUnit() =>
        new(
            Guid.NewGuid(),
            new Address("unit", UnitId),
            new Address("agent", AgentId),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { task = "do-it" }),
            DateTimeOffset.UtcNow);

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