// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
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
/// Tests for <see cref="AgentActor"/>'s <c>OnActivateAsync</c> seed
/// path that auto-applies expertise declared in
/// <c>AgentDefinition</c> YAML on first activation. See #488.
///
/// Per ADR-0040 / #2048 the "actor-state wins" precedence flag lives
/// on <c>agent_live_config.expertise_initialised</c>. These tests
/// drive the EF surface through <see cref="InMemoryAgentLiveConfigStore"/>.
/// </summary>
public class AgentActorSeedExpertiseTests
{
    private static readonly Guid AgentGuid = Guid.NewGuid();
    private static readonly string AgentId = GuidFormatter.Format(AgentGuid);

    [Fact]
    public async Task OnActivateAsync_NoPriorState_SeedsFromProvider()
    {
        var seed = new[]
        {
            new ExpertiseDomain("architecture", string.Empty, ExpertiseLevel.Expert),
            new ExpertiseDomain("code-review", string.Empty, ExpertiseLevel.Expert),
        };

        var (actor, store) = BuildActor(
            expertiseInitialised: false,
            seedProvider: CreateSeedProvider(seed));

        await InvokeOnActivateAsync(actor);

        var stored = await store.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Length.ShouldBe(2);
        stored.ShouldContain(d => d.Name == "architecture");
        stored.ShouldContain(d => d.Name == "code-review");
        (await store.HasExpertiseSetAsync(AgentGuid, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// Precedence: operator state wins. Once anything has been
    /// persisted (even an empty list), subsequent activations must not
    /// re-seed from YAML so runtime operator edits are never silently
    /// clobbered by a stale seed.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_ExpertiseAlreadyInitialised_DoesNotOverwrite()
    {
        var (actor, store) = BuildActor(
            expertiseInitialised: true,
            seedProvider: CreateSeedProvider(new[]
            {
                new ExpertiseDomain("should-not-seed", string.Empty, ExpertiseLevel.Beginner),
            }));

        await InvokeOnActivateAsync(actor);

        var stored = await store.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnActivateAsync_NoSeedDeclared_DoesNotWrite()
    {
        var (actor, store) = BuildActor(
            expertiseInitialised: false,
            seedProvider: CreateSeedProvider(null));

        await InvokeOnActivateAsync(actor);

        var stored = await store.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.ShouldBeEmpty();
        (await store.HasExpertiseSetAsync(AgentGuid, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task OnActivateAsync_EmptySeedList_DoesNotWrite()
    {
        // A declared-but-empty seed is a legal operator choice ("explicitly
        // no seed"); treat it identically to "no seed declared" at the
        // actor layer — there is nothing to write.
        var (actor, store) = BuildActor(
            expertiseInitialised: false,
            seedProvider: CreateSeedProvider(Array.Empty<ExpertiseDomain>()));

        await InvokeOnActivateAsync(actor);

        var stored = await store.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.ShouldBeEmpty();
        (await store.HasExpertiseSetAsync(AgentGuid, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task OnActivateAsync_NullProvider_NoOp()
    {
        // Legacy test harnesses pass null for the seed provider —
        // activation must remain a no-op in that case.
        var (actor, store) = BuildActor(expertiseInitialised: false, seedProvider: null);

        await InvokeOnActivateAsync(actor);

        var stored = await store.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.ShouldBeEmpty();
    }

    private static (AgentActor Actor, InMemoryAgentLiveConfigStore Store) BuildActor(
        bool expertiseInitialised,
        IExpertiseSeedProvider? seedProvider)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var stateManager = Substitute.For<IActorStateManager>();
        var store = new InMemoryAgentLiveConfigStore();
        if (expertiseInitialised)
        {
            store.SetExpertiseInitialised(AgentGuid, true);
        }

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentId),
        });

        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());

        var membership = Substitute.For<IUnitMembershipRepository>();
        membership
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var policyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
        policyEnforcer.WithAllowByDefault();

        // Wire the real AgentLifecycleCoordinator so that OnActivateAsync
        // exercises the coordinator's seeding logic end-to-end.
        var lifecycleCoordinator = new AgentLifecycleCoordinator(
            Substitute.For<ILogger<AgentLifecycleCoordinator>>());

        var actor = new AgentActor(
            host,
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(
                Substitute.For<IExecutionDispatcher>(),
                router,
                Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            Substitute.For<IAgentDefinitionProvider>(),
            Array.Empty<ISkillRegistry>(),
            membership,
            policyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            lifecycleCoordinator,
            new AgentStateCoordinator(store, Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()),
            seedProvider);

        typeof(Actor).GetField("<StateManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor, stateManager);

        return (actor, store);
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
