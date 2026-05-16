// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Agents;
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
/// Tests for the <see cref="AgentActor"/> metadata surface introduced
/// in #124. Covers partial PATCH semantics, enabled / execution-mode
/// persistence, and explicit parent-unit clearing.
///
/// Per ADR-0040 / #2048 the agent live-config keys live on the
/// <c>agent_live_config</c> EF row, so these tests drive the actor
/// through an in-memory <see cref="IAgentLiveConfigStore"/> and assert
/// against the same store.
/// </summary>
public class AgentMetadataTests
{
    private static readonly Guid AgentGuid = Guid.NewGuid();
    private static readonly string AgentId = GuidFormatter.Format(AgentGuid);

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly InMemoryAgentLiveConfigStore _liveConfigStore = new();
    private readonly AgentActor _actor;

    public AgentMetadataTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(AgentId),
        });

        var membershipRepository = Substitute.For<IUnitMembershipRepository>();
        membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>().WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(
                Substitute.For<IExecutionDispatcher>(),
                Substitute.For<MessageRouter>(
                    Substitute.For<Cvoya.Spring.Core.Directory.IDirectoryService>(),
                    Substitute.For<Cvoya.Spring.Dapr.Routing.IAgentProxyResolver>(),
                    Substitute.For<Cvoya.Spring.Dapr.Auth.IPermissionService>(),
                    loggerFactory,
                    NullMessageWriterScopeFactory.Create()),
                Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            Substitute.For<IAgentDefinitionProvider>(),
            new List<ISkillRegistry>(),
            membershipRepository,
            unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(_liveConfigStore, Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));
        SetStateManager(_actor, _stateManager);
    }

    [Fact]
    public async Task GetMetadataAsync_NothingPersisted_ReturnsAllNulls()
    {
        var metadata = await _actor.GetMetadataAsync(TestContext.Current.CancellationToken);

        metadata.Model.ShouldBeNull();
        metadata.Specialty.ShouldBeNull();
        metadata.Enabled.ShouldBeNull();
        metadata.ExecutionMode.ShouldBeNull();
        metadata.ParentUnit.ShouldBeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_AllFieldsProvided_WritesEachToEf()
    {
        var patch = new AgentMetadata(
            Model: "claude-opus",
            Specialty: "reviewer",
            Enabled: false,
            ExecutionMode: AgentExecutionMode.OnDemand,
            ParentUnit: "engineering");

        await _actor.SetMetadataAsync(patch, TestContext.Current.CancellationToken);

        var stored = await _liveConfigStore.GetMetadataAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Model.ShouldBe("claude-opus");
        stored.Specialty.ShouldBe("reviewer");
        stored.Enabled.ShouldBe(false);
        stored.ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
        // ADR-0040: ParentUnit is owned by unit_memberships and ignored here.
        stored.ParentUnit.ShouldBeNull();

        // ADR-0040: writes never touch the actor state manager.
        await _stateManager.DidNotReceive().SetStateAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("metadata updated")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_OnlyModel_LeavesOtherFieldsUntouched()
    {
        // Pre-seed something we expect to be preserved.
        _liveConfigStore.SeedMetadata(AgentGuid, new AgentMetadata(
            Specialty: "reviewer", Enabled: true, ExecutionMode: AgentExecutionMode.Auto));

        var patch = new AgentMetadata(Model: "gpt-4o");

        await _actor.SetMetadataAsync(patch, TestContext.Current.CancellationToken);

        var stored = await _liveConfigStore.GetMetadataAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Model.ShouldBe("gpt-4o");
        stored.Specialty.ShouldBe("reviewer");
        stored.Enabled.ShouldBe(true);
        stored.ExecutionMode.ShouldBe(AgentExecutionMode.Auto);
    }

    [Fact]
    public async Task SetMetadataAsync_AllFieldsNull_IsNoopAndEmitsNoEvent()
    {
        var empty = new AgentMetadata();

        await _actor.SetMetadataAsync(empty, TestContext.Current.CancellationToken);

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_OnlyParentUnit_IsNoopAndEmitsNoEvent()
    {
        // ParentUnit alone is not enough to persist anything (membership
        // table is the source of truth) — the patch is a no-op.
        var patch = new AgentMetadata(ParentUnit: "engineering");

        await _actor.SetMetadataAsync(patch, TestContext.Current.CancellationToken);

        var stored = await _liveConfigStore.GetMetadataAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Model.ShouldBeNull();
        stored.Specialty.ShouldBeNull();

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearParentUnitAsync_EmitsAuditEventAndDoesNotMutateLiveConfig()
    {
        // Pre-seed model so we can verify it's preserved across the
        // (now no-op for live-config) call.
        _liveConfigStore.SeedMetadata(AgentGuid, new AgentMetadata(Model: "claude-opus"));

        await _actor.ClearParentUnitAsync(TestContext.Current.CancellationToken);

        var stored = await _liveConfigStore.GetMetadataAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Model.ShouldBe("claude-opus");

        // The membership row is what actually changes — that lives on
        // unit_memberships, exercised by the unit unassign endpoint.
        // The actor's job here is only the audit event.
        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("parent-unit cleared")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetExpertiseAsync_PersistsListAndEmitsEvent()
    {
        var input = new[]
        {
            new ExpertiseDomain("python/fastapi", "API authoring", ExpertiseLevel.Expert),
            new ExpertiseDomain("python/fastapi", "API authoring", ExpertiseLevel.Advanced),
        };

        await _actor.SetExpertiseAsync(input, TestContext.Current.CancellationToken);

        var stored = await _liveConfigStore.GetExpertiseAsync(AgentGuid, TestContext.Current.CancellationToken);
        stored.Length.ShouldBe(1);
        stored[0].Name.ShouldBe("python/fastapi");
        // Last write wins on case-insensitive name match.
        stored[0].Level.ShouldBe(ExpertiseLevel.Advanced);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("expertise replaced")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetExpertiseAsync_EmptyList_FlipsExpertiseInitialisedFlag()
    {
        await _actor.SetExpertiseAsync(Array.Empty<ExpertiseDomain>(), TestContext.Current.CancellationToken);

        // Empty list still counts as an explicit operator choice — the
        // activation seeder must not re-apply the YAML seed afterwards.
        var initialised = await _liveConfigStore.HasExpertiseSetAsync(AgentGuid, TestContext.Current.CancellationToken);
        initialised.ShouldBeTrue();
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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
