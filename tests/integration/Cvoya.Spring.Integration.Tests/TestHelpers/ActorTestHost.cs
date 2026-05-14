// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Reflection;

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
using Cvoya.Spring.Dapr.Units;

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
            ActorId = new ActorId(NormaliseActorId(actorId))
        });

        var activityEventBus = Substitute.For<IActivityEventBus>();
        var initiativeEngine = Substitute.For<IInitiativeEngine>();
        var policyStore = Substitute.For<IAgentPolicyStore>();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());
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
            definitionProvider,
            Substitute.For<ILogger<Cvoya.Spring.Dapr.Initiative.AgentObservationCoordinator>>());

        // ADR-0040 / #2048: AgentStateCoordinator routes through the EF
        // store. Configure the substitute to return defaults that mirror
        // a "no row yet" agent — otherwise NSubstitute returns
        // default(Task<AgentMetadata>) (i.e. null) which crashes the
        // agent's effective-metadata merge.
        var liveConfigStore = Substitute.For<IAgentLiveConfigStore>();
        liveConfigStore
            .GetMetadataAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Agents.AgentMetadata());
        liveConfigStore
            .GetSkillsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        liveConfigStore
            .GetExpertiseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Cvoya.Spring.Core.Capabilities.ExpertiseDomain>());

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
            new AgentStateCoordinator(liveConfigStore, Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            directoryService: directoryService);
        SetStateManager(actor, stateManager);

        // Default: no per-thread channels, no pending amendments. The
        // per-thread channel keys are <c>Agent:Channel:{ThreadId}</c>;
        // tests that exercise the routing path either let the substitute
        // return its default ConditionalValue (HasValue=false) for any
        // such key, or arrange specific thread ids explicitly.
        stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));
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
    /// Creates a <see cref="UnitActor"/> with a mocked state manager and runtime invocation path.
    /// </summary>
    /// <param name="runtimeInvocationPath">The runtime invocation path to use. If null, a substitute is created.</param>
    /// <param name="actorId">The actor identifier. Defaults to a new GUID.</param>
    /// <param name="directoryService">The directory service used for nested-unit cycle detection. Defaults to a substitute that resolves nothing.</param>
    /// <param name="actorProxyFactory">The actor proxy factory used for nested-unit cycle detection. Defaults to a substitute.</param>
    /// <param name="memberGraphStore">
    /// Optional EF-backed member graph store (#2052 / ADR-0040). When
    /// <see langword="null"/>, an in-memory store is created so tests
    /// can pre-seed agent / sub-unit edges via the returned reference.
    /// </param>
    /// <returns>A tuple of the actor instance, its mocked state manager, and the runtime invocation path.</returns>
    public static (UnitActor Actor, IActorStateManager StateManager, IRuntimeInvocationPath RuntimeInvocationPath, InMemoryUnitMemberGraphStore MemberGraphStore) CreateUnitActor(
        IRuntimeInvocationPath? runtimeInvocationPath = null,
        string? actorId = null,
        IDirectoryService? directoryService = null,
        IActorProxyFactory? actorProxyFactory = null,
        InMemoryUnitMemberGraphStore? memberGraphStore = null)
    {
        var harness = CreateUnitActorWithHarness(
            runtimeInvocationPath,
            actorId,
            directoryService,
            actorProxyFactory,
            memberGraphStore);

        return (harness.Actor, harness.StateManager, harness.RuntimeInvocationPath, harness.MemberGraphStore);
    }

    /// <summary>
    /// Creates a <see cref="UnitActor"/> together with the mocked
    /// collaborators integration tests need to observe activity events or
    /// arrange member-graph state.
    /// </summary>
    public static UnitActorTestHarness CreateUnitActorWithHarness(
        IRuntimeInvocationPath? runtimeInvocationPath = null,
        string? actorId = null,
        IDirectoryService? directoryService = null,
        IActorProxyFactory? actorProxyFactory = null,
        InMemoryUnitMemberGraphStore? memberGraphStore = null)
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var createdRuntimeInvocationPath = runtimeInvocationPath is null;
        if (createdRuntimeInvocationPath)
        {
            runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
            runtimeInvocationPath
                .InvokeAsync(
                    Arg.Any<CoreMessaging.Address>(),
                    Arg.Any<Message>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>())
                .Returns(Task.CompletedTask);
        }
        directoryService ??= Substitute.For<IDirectoryService>();
        actorProxyFactory ??= Substitute.For<IActorProxyFactory>();

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(NormaliseActorId(actorId))
        });

        var activityEventBus = Substitute.For<Core.Capabilities.IActivityEventBus>();

        // ADR-0040 / #2049: UnitStateCoordinator routes through the EF
        // store. Configure the substitute to return defaults that mirror
        // a "no row yet" unit — otherwise NSubstitute returns
        // default(Task<UnitMetadata>) (i.e. null) which crashes the
        // actor's metadata read.
        var stateCoordinator = Substitute.For<IUnitStateCoordinator>();
        stateCoordinator
            .GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        stateCoordinator
            .GetBoundaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Capabilities.UnitBoundary.Empty);
        stateCoordinator
            .GetPermissionInheritanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        stateCoordinator
            .GetOwnExpertiseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Cvoya.Spring.Core.Capabilities.ExpertiseDomain>());
        stateCoordinator
            .HasOwnExpertiseSetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        memberGraphStore ??= new InMemoryUnitMemberGraphStore();

        var actor = new UnitActor(
            host,
            loggerFactory,
            runtimeInvocationPath!,
            activityEventBus,
            directoryService,
            actorProxyFactory,
            stateCoordinator,
            memberGraphStore);
        SetStateManager(actor, stateManager);

        if (createdRuntimeInvocationPath)
        {
            runtimeInvocationPath!.ClearReceivedCalls();
        }
        return new UnitActorTestHarness(
            actor,
            stateManager,
            runtimeInvocationPath!,
            memberGraphStore,
            activityEventBus);
    }

    /// <summary>
    /// Bundles a <see cref="UnitActor"/> with the mocks integration tests
    /// commonly inspect.
    /// </summary>
    public sealed record UnitActorTestHarness(
        UnitActor Actor,
        IActorStateManager StateManager,
        IRuntimeInvocationPath RuntimeInvocationPath,
        InMemoryUnitMemberGraphStore MemberGraphStore,
        IActivityEventBus ActivityEventBus);

    /// <summary>
    /// Normalises a caller-supplied actor id so the resulting <c>ActorId</c>
    /// is always a valid Guid string. Accepts canonical or dashed Guids
    /// verbatim and routes any legacy slug literal through
    /// <see cref="TestSlugIds.HexFor"/> for stable cross-call identity.
    /// </summary>
    private static string NormaliseActorId(string? actorId)
    {
        if (string.IsNullOrEmpty(actorId))
        {
            return Guid.NewGuid().ToString("N");
        }

        return Guid.TryParse(actorId, out var g)
            ? g.ToString("N")
            : TestSlugIds.HexFor(actorId);
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
