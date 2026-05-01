// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using CoreMessaging = Cvoya.Spring.Core.Messaging;

/// <summary>
/// Helper to create actor instances with mocked state managers for integration testing.
/// Wraps the boilerplate of creating <see cref="ActorHost"/> via <c>ActorHost.CreateForTest</c>
/// and wiring up <see cref="IActorStateManager"/> mocks.
/// </summary>
public static class ActorTestHost
{
    /// <summary>
    /// Creates an <see cref="AgentActor"/> with a mocked state manager preconfigured
    /// with empty active conversation and pending conversations.
    /// </summary>
    /// <param name="actorId">The actor identifier. Defaults to a new GUID.</param>
    /// <returns>A tuple of the actor instance and its mocked state manager.</returns>
    public static (AgentActor Actor, IActorStateManager StateManager) CreateAgentActor(string? actorId = null)
    {
        var harness = CreateAgentActorWithHarness(actorId);
        return (harness.Actor, harness.StateManager);
    }

    /// <summary>
    /// Creates an <see cref="AgentActor"/> together with its mocked
    /// collaborators so integration tests can arrange behaviour on the
    /// membership repository, unit-policy enforcer, reflection-action
    /// registry, or activity bus without reaching into private fields.
    /// </summary>
    /// <param name="actorId">
    /// The actor identifier. Defaults to a new GUID. Use a UUID string when
    /// the test involves membership lookups (#1492: membership table is UUID-keyed).
    /// </param>
    /// <param name="directoryService">
    /// Optional directory service. Required for amendment sender authorisation
    /// when the amendment originates from a unit (#1492: slug → UUID resolution).
    /// </param>
    public static AgentActorTestHarness CreateAgentActorWithHarness(
        string? actorId = null,
        IDirectoryService? directoryService = null)
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId ?? Guid.NewGuid().ToString())
        });

        var activityEventBus = Substitute.For<IActivityEventBus>();
        var initiativeEngine = Substitute.For<IInitiativeEngine>();
        var policyStore = Substitute.For<IAgentPolicyStore>();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory);
        var definitionProvider = Substitute.For<IAgentDefinitionProvider>();
        var membershipRepository = Substitute.For<IUnitMembershipRepository>();
        membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);
        var reflectionRegistry = Substitute.For<IReflectionActionHandlerRegistry>();
        reflectionRegistry.Find(Arg.Any<string?>()).Returns((IReflectionActionHandler?)null);
        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
        unitPolicyEnforcer
            .EvaluateSkillInvocationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        unitPolicyEnforcer
            .EvaluateModelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        unitPolicyEnforcer
            .EvaluateCostAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        unitPolicyEnforcer
            .EvaluateExecutionModeAsync(Arg.Any<string>(), Arg.Any<Cvoya.Spring.Core.Agents.AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        unitPolicyEnforcer
            .ResolveExecutionModeAsync(Arg.Any<string>(), Arg.Any<Cvoya.Spring.Core.Agents.AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(ci => ExecutionModeResolution.AllowAsIs(ci.ArgAt<Cvoya.Spring.Core.Agents.AgentExecutionMode>(1)));
        unitPolicyEnforcer
            .EvaluateInitiativeActionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);

        // Wire the real AgentObservationCoordinator with mocked seams so
        // integration tests exercise the coordinator's end-to-end path.
        // The scoped seams (IUnitPolicyEnforcer, IAgentInitiativeEvaluator)
        // are owned by AgentActor and passed to the coordinator as delegates.
        var initiativeEvaluator = Substitute.For<IAgentInitiativeEvaluator>();
        initiativeEvaluator
            .EvaluateAsync(Arg.Any<InitiativeEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeEvaluationResult.Autonomously(InitiativeLevel.Autonomous));
        var observationCoordinator = new Cvoya.Spring.Dapr.Initiative.AgentObservationCoordinator(
            initiativeEngine,
            reflectionRegistry,
            router,
            Substitute.For<ILogger<Cvoya.Spring.Dapr.Initiative.AgentObservationCoordinator>>());

        var actor = new AgentActor(
            host,
            activityEventBus,
            observationCoordinator,
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(dispatcher, router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            definitionProvider,
            Array.Empty<ISkillRegistry>(),
            membershipRepository,
            unitPolicyEnforcer,
            initiativeEvaluator,
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            directoryService: directoryService);
        SetStateManager(actor, stateManager);

        // Default: no active conversation, no pending conversations, no pending amendments.
        stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));
        stateManager.TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ThreadChannel>>(false, default!));
        stateManager.TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<PendingAmendment>>(false, default!));

        return new AgentActorTestHarness(
            actor, stateManager, activityEventBus, membershipRepository,
            reflectionRegistry, unitPolicyEnforcer);
    }

    /// <summary>
    /// Bundles an <see cref="AgentActor"/> instance with the mocks integration
    /// tests typically need to arrange. Keeps tests free of reflection into
    /// private fields.
    /// </summary>
    public sealed record AgentActorTestHarness(
        AgentActor Actor,
        IActorStateManager StateManager,
        IActivityEventBus ActivityEventBus,
        IUnitMembershipRepository MembershipRepository,
        IReflectionActionHandlerRegistry ReflectionRegistry,
        IUnitPolicyEnforcer UnitPolicyEnforcer);

    /// <summary>
    /// Creates a <see cref="UnitActor"/> with a mocked state manager and orchestration strategy.
    /// </summary>
    /// <param name="strategy">The orchestration strategy to use. If null, a substitute is created.</param>
    /// <param name="actorId">The actor identifier. Defaults to a new GUID.</param>
    /// <param name="directoryService">The directory service used for nested-unit cycle detection. Defaults to a substitute that resolves nothing.</param>
    /// <param name="actorProxyFactory">The actor proxy factory used for nested-unit cycle detection. Defaults to a substitute.</param>
    /// <returns>A tuple of the actor instance, its mocked state manager, and the orchestration strategy.</returns>
    public static (UnitActor Actor, IActorStateManager StateManager, IOrchestrationStrategy Strategy) CreateUnitActor(
        IOrchestrationStrategy? strategy = null,
        string? actorId = null,
        IDirectoryService? directoryService = null,
        IActorProxyFactory? actorProxyFactory = null)
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        strategy ??= Substitute.For<IOrchestrationStrategy>();
        directoryService ??= Substitute.For<IDirectoryService>();
        actorProxyFactory ??= Substitute.For<IActorProxyFactory>();

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId ?? Guid.NewGuid().ToString())
        });

        var activityEventBus = Substitute.For<Core.Capabilities.IActivityEventBus>();
        var actor = new UnitActor(
            host,
            loggerFactory,
            strategy,
            activityEventBus,
            directoryService,
            actorProxyFactory);
        SetStateManager(actor, stateManager);

        // Default: no members.
        stateManager.TryGetStateAsync<List<CoreMessaging.Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<CoreMessaging.Address>>(false, default!));

        return (actor, stateManager, strategy);
    }

    /// <summary>
    /// Creates a <see cref="UnitActor"/> with an <see cref="IOrchestrationStrategyResolver"/>
    /// wired (the production path added by #491) so tests can assert the
    /// actor consults the resolver per message. The constructor-injected
    /// unkeyed strategy is still supplied because the actor signature keeps
    /// it as the legacy fallback — test assertions can verify it is NOT
    /// called when the resolver is present.
    /// </summary>
    public static (UnitActor Actor, IActorStateManager StateManager, IOrchestrationStrategyResolver Resolver, IOrchestrationStrategy FallbackStrategy) CreateUnitActorWithResolver(
        IOrchestrationStrategyResolver resolver,
        string? actorId = null,
        IDirectoryService? directoryService = null,
        IActorProxyFactory? actorProxyFactory = null)
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var fallbackStrategy = Substitute.For<IOrchestrationStrategy>();
        directoryService ??= Substitute.For<IDirectoryService>();
        actorProxyFactory ??= Substitute.For<IActorProxyFactory>();

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId ?? Guid.NewGuid().ToString()),
        });

        var activityEventBus = Substitute.For<Core.Capabilities.IActivityEventBus>();
        var actor = new UnitActor(
            host,
            loggerFactory,
            fallbackStrategy,
            activityEventBus,
            directoryService,
            actorProxyFactory,
            expertiseSeedProvider: null,
            strategyResolver: resolver);
        SetStateManager(actor, stateManager);

        stateManager.TryGetStateAsync<List<CoreMessaging.Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<CoreMessaging.Address>>(false, default!));

        return (actor, stateManager, resolver, fallbackStrategy);
    }

    /// <summary>
    /// Sets the state manager on a Dapr actor instance using reflection.
    /// </summary>
    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
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