// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

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
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="AgentActor"/>'s <c>OnActivateAsync</c> seed path that
/// auto-applies expertise declared in <c>AgentDefinition</c> YAML to actor
/// state on first activation. See #488.
/// </summary>
public class AgentActorSeedExpertiseTests
{
    [Fact]
    public async Task OnActivateAsync_EmptyState_SeedsFromProvider()
    {
        var seed = new[]
        {
            new ExpertiseDomain("architecture", "", ExpertiseLevel.Expert),
            new ExpertiseDomain("code-review", "", ExpertiseLevel.Expert),
        };

        var actor = BuildActor(
            stateHasValue: false,
            seedProvider: CreateSeedProvider(seed),
            out var stateManager);

        List<ExpertiseDomain>? captured = null;
        stateManager.SetStateAsync(
                StateKeys.AgentExpertise,
                Arg.Do<List<ExpertiseDomain>>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await InvokeOnActivateAsync(actor);

        captured.ShouldNotBeNull();
        captured!.Count.ShouldBe(2);
        captured[0].Name.ShouldBe("architecture");
        captured[1].Name.ShouldBe("code-review");
    }

    /// <summary>
    /// Precedence: actor state wins. Once anything has been persisted (even
    /// an empty list), subsequent activations must not re-seed from YAML so
    /// runtime operator edits are never silently clobbered by a stale seed.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_StateAlreadySet_DoesNotOverwrite()
    {
        var actor = BuildActor(
            stateHasValue: true,
            seedProvider: CreateSeedProvider(new[]
            {
                new ExpertiseDomain("should-not-seed", "", ExpertiseLevel.Beginner),
            }),
            out var stateManager);

        await InvokeOnActivateAsync(actor);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnActivateAsync_NoSeedDeclared_DoesNotWrite()
    {
        var actor = BuildActor(
            stateHasValue: false,
            seedProvider: CreateSeedProvider(null),
            out var stateManager);

        await InvokeOnActivateAsync(actor);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnActivateAsync_EmptySeedList_DoesNotWrite()
    {
        // A declared-but-empty seed is a legal operator choice ("explicitly
        // no seed"); treat it identically to "no seed declared" at the actor
        // layer — there is nothing to write.
        var actor = BuildActor(
            stateHasValue: false,
            seedProvider: CreateSeedProvider(Array.Empty<ExpertiseDomain>()),
            out var stateManager);

        await InvokeOnActivateAsync(actor);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnActivateAsync_NullProvider_NoOp()
    {
        // Legacy test harnesses pass null for the seed provider — activation
        // must remain a no-op in that case.
        var actor = BuildActor(stateHasValue: false, seedProvider: null, out var stateManager);

        await InvokeOnActivateAsync(actor);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    private static AgentActor BuildActor(
        bool stateHasValue,
        IExpertiseSeedProvider? seedProvider,
        out IActorStateManager stateManager)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        stateManager = Substitute.For<IActorStateManager>();
        stateManager
            .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.AgentExpertise, Arg.Any<CancellationToken>())
            .Returns(stateHasValue
                ? new ConditionalValue<List<ExpertiseDomain>>(true, new List<ExpertiseDomain>())
                : new ConditionalValue<List<ExpertiseDomain>>(false, default!));

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId("seed-agent"),
        });

        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory);

        var membership = Substitute.For<IUnitMembershipRepository>();
        membership
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var policyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
        policyEnforcer.WithAllowByDefault();
        var initiativeEvaluator = Substitute.For<IAgentInitiativeEvaluator>().WithActAutonomouslyByDefault();

        var actor = new AgentActor(
            host,
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IInitiativeEngine>(),
            Substitute.For<IAgentPolicyStore>(),
            Substitute.For<IExecutionDispatcher>(),
            router,
            Substitute.For<IAgentDefinitionProvider>(),
            Array.Empty<ISkillRegistry>(),
            membership,
            Substitute.For<IReflectionActionHandlerRegistry>(),
            policyEnforcer,
            initiativeEvaluator,
            loggerFactory,
            seedProvider);

        typeof(Actor).GetField("<StateManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor, stateManager);

        return actor;
    }

    private static IExpertiseSeedProvider CreateSeedProvider(IReadOnlyList<ExpertiseDomain>? seed)
    {
        var provider = Substitute.For<IExpertiseSeedProvider>();
        provider
            .GetAgentSeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(seed);
        return provider;
    }

    private static Task InvokeOnActivateAsync(AgentActor actor)
    {
        var method = typeof(AgentActor).GetMethod(
            "OnActivateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();
        return (Task)method!.Invoke(actor, Array.Empty<object>())!;
    }
}