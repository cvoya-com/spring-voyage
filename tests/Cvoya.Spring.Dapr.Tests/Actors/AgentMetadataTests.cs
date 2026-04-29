// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
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
/// Tests for the <see cref="AgentActor"/> metadata surface introduced in #124.
/// Covers partial PATCH semantics, enabled / execution-mode persistence, and
/// explicit parent-unit clearing (separated from the partial-patch path so
/// clearing is unambiguous).
/// </summary>
public class AgentMetadataTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly AgentActor _actor;

    public AgentMetadataTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-agent"),
        });

        // Default: no state set for any metadata key.
        _stateManager.TryGetStateAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
        _stateManager.TryGetStateAsync<bool>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<bool>(false, default));
        _stateManager.TryGetStateAsync<AgentExecutionMode>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<AgentExecutionMode>(false, default));

        var membershipRepository = Substitute.For<IUnitMembershipRepository>();
        membershipRepository
            .GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>().WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            Substitute.For<IExecutionDispatcher>(),
            Substitute.For<MessageRouter>(
                Substitute.For<Cvoya.Spring.Core.Directory.IDirectoryService>(),
                Substitute.For<Cvoya.Spring.Dapr.Routing.IAgentProxyResolver>(),
                Substitute.For<Cvoya.Spring.Dapr.Auth.IPermissionService>(),
                loggerFactory),
            Substitute.For<IAgentDefinitionProvider>(),
            new List<ISkillRegistry>(),
            membershipRepository,
            unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>());
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
    public async Task SetMetadataAsync_AllFieldsProvided_WritesEach()
    {
        var patch = new AgentMetadata(
            Model: "claude-opus",
            Specialty: "reviewer",
            Enabled: false,
            ExecutionMode: AgentExecutionMode.OnDemand,
            ParentUnit: "engineering");

        await _actor.SetMetadataAsync(patch, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentModel, "claude-opus", Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentSpecialty, "reviewer", Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentEnabled, false, Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentExecutionMode, AgentExecutionMode.OnDemand, Arg.Any<CancellationToken>());
        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentParentUnit, "engineering", Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("metadata updated")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_OnlyModel_LeavesOtherFieldsUntouched()
    {
        var patch = new AgentMetadata(Model: "gpt-4o");

        await _actor.SetMetadataAsync(patch, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentModel, "gpt-4o", Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentSpecialty, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentEnabled, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentExecutionMode, Arg.Any<AgentExecutionMode>(), Arg.Any<CancellationToken>());
        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentParentUnit, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetMetadataAsync_AllFieldsNull_IsNoopAndEmitsNoEvent()
    {
        var empty = new AgentMetadata();

        await _actor.SetMetadataAsync(empty, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearParentUnitAsync_RemovesStateAndEmitsEvent()
    {
        await _actor.ClearParentUnitAsync(TestContext.Current.CancellationToken);

        await _stateManager.Received(1).RemoveStateAsync(
            StateKeys.AgentParentUnit, Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("parent-unit cleared")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSkillsAsync_NothingPersisted_ReturnsEmpty()
    {
        _stateManager.TryGetStateAsync<List<string>>(StateKeys.AgentSkills, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));

        var skills = await _actor.GetSkillsAsync(TestContext.Current.CancellationToken);

        skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetSkillsAsync_WritesNormalisedListAndEmitsEvent()
    {
        // Input has duplicates, whitespace, and unstable order — the
        // persisted list must be deduped, trimmed, and ordinal-sorted.
        var input = new[] { " github_write_file ", "github_read_file", "github_write_file", "" };

        await _actor.SetSkillsAsync(input, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentSkills,
            Arg.Is<List<string>>(l =>
                l.Count == 2 &&
                l[0] == "github_read_file" &&
                l[1] == "github_write_file"),
            Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Summary.Contains("skills replaced")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSkillsAsync_EmptyList_PersistsExplicitClear()
    {
        // Empty list is not "leave alone" — it's a meaningful configured
        // state (agent has no skills enabled). Must persist and emit.
        await _actor.SetSkillsAsync(Array.Empty<string>(), TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.AgentSkills,
            Arg.Is<List<string>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());

        await _activityEventBus.Received().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetSkillsAsync_Null_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _actor.SetSkillsAsync(null!, TestContext.Current.CancellationToken));
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        // Mirrors the helper in UnitActorTests: Actor.StateManager is a
        // protected property — reach through the compiler-generated backing
        // field, falling back to the property setter if the SDK ever changes.
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