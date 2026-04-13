// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

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
        var actor = new AgentActor(
            host,
            activityEventBus,
            initiativeEngine,
            policyStore,
            dispatcher,
            router,
            definitionProvider,
            Array.Empty<ISkillRegistry>(),
            loggerFactory);
        SetStateManager(actor, stateManager);

        // Default: no active conversation, no pending conversations.
        stateManager.TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConversationChannel>(false, default!));
        stateManager.TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ConversationChannel>>(false, default!));

        return (actor, stateManager);
    }

    /// <summary>
    /// Creates a <see cref="UnitActor"/> with a mocked state manager and orchestration strategy.
    /// </summary>
    /// <param name="strategy">The orchestration strategy to use. If null, a substitute is created.</param>
    /// <param name="actorId">The actor identifier. Defaults to a new GUID.</param>
    /// <returns>A tuple of the actor instance, its mocked state manager, and the orchestration strategy.</returns>
    public static (UnitActor Actor, IActorStateManager StateManager, IOrchestrationStrategy Strategy) CreateUnitActor(
        IOrchestrationStrategy? strategy = null,
        string? actorId = null)
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        strategy ??= Substitute.For<IOrchestrationStrategy>();

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId ?? Guid.NewGuid().ToString())
        });

        var activityEventBus = Substitute.For<Core.Capabilities.IActivityEventBus>();
        var actor = new UnitActor(host, loggerFactory, strategy, activityEventBus);
        SetStateManager(actor, stateManager);

        // Default: no members.
        stateManager.TryGetStateAsync<List<Core.Messaging.Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Core.Messaging.Address>>(false, default!));

        return (actor, stateManager, strategy);
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