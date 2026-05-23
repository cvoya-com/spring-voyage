// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests that verify <see cref="AgentActor.HandleDomainMessageAsync"/> invokes
/// <see cref="IExecutionDispatcher"/> and emits the ADR-0056 §7 runtime
/// lifecycle activities (MessageDispatchedToRuntime, RuntimeStarted, and one
/// of the typed terminals — RuntimeCompleted / RuntimeFailed /
/// RuntimeCompletedSilent). The dispatcher no longer returns a Message; the
/// "dispatch response recorded on thread" persistence path is deleted
/// (ADR-0056 §9). Runtimes that want to reply on the thread call
/// <c>sv.messaging.send</c>; the messaging tool persists its own row and
/// emits its own MessageSent activity.
/// </summary>
public class AgentActorDispatchTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly ISkillRegistry _skillRegistry = Substitute.For<ISkillRegistry>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly AgentActor _actor;

    public AgentActorDispatchTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _skillRegistry.Name.Returns("github");
        _skillRegistry.GetToolDefinitions().Returns([
            new ToolDefinition("github.comment", "comment", JsonSerializer.SerializeToElement(new { }), string.Empty)
        ]);

        _definitionProvider.GetByIdAsync(TestSlugIds.HexFor("test-agent"), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(TestSlugIds.HexFor("test-agent"), "Test", "Agent instructions", null));

        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestSlugIds.HexFor("test-agent"))
        });

        _membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>().WithAllowByDefault();

        _actor = new AgentActor(
            host,
            _activityEventBus,
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(_dispatcher, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            _definitionProvider,
            [_skillRegistry],
            _membershipRepository,
            unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(new InMemoryAgentLiveConfigStore(), Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));
        SetStateManager(_actor, _stateManager);

        _stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));
    }

    private static Message CreateDomainMessage(string threadId = "conv-1")
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("unit", TestSlugIds.HexFor("my-unit")),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement(new { task = "do-it" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task NewConversation_SpawnsDispatchWithAssembledContext()
    {
        var message = CreateDomainMessage();

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent());

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == message.Id),
            Arg.Is<PromptAssemblyContext?>(ctx =>
                ctx != null &&
                ctx.Skills != null && ctx.Skills.Count == 1 &&
                ctx.Skills[0].Name == "github" &&
                ctx.AgentInstructions == "Agent instructions"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfulDispatch_EmitsRuntimeCompletedActivity()
    {
        var message = CreateDomainMessage();
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success(toolCallCount: 1));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.RuntimeCompleted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SilentDispatch_EmitsRuntimeCompletedSilentActivity()
    {
        // ADR-0056 §5: clean exit with no tool calls surfaces as
        // RuntimeCompletedSilent — never auto-wrapped into a synthesised
        // message.
        var message = CreateDomainMessage();
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent());

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.EventType == ActivityEventType.RuntimeCompletedSilent),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonZeroExitDispatch_EmitsRuntimeFailedActivity()
    {
        var message = CreateDomainMessage();
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Failure(exitCode: 42));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.RuntimeFailed
                && e.Severity == ActivitySeverity.Error),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatcherThrows_ErrorIsLoggedNotPropagated()
    {
        var message = CreateDomainMessage();

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RuntimeOutcome>>(_ => throw new InvalidOperationException("dispatcher failed"));

        // Actor turn must still return an ack even when the fire-and-forget dispatch task fails.
        var ack = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        ack.ShouldNotBeNull();

        // Awaiting the dispatch task should not surface the exception (it's logged + swallowed).
        var act = () => _actor.PendingDispatchTask!;
        await Should.NotThrowAsync(act);
    }

    [Fact]
    public async Task SecondMessageSameConversation_DoesNotDispatchAgain()
    {
        var message1 = CreateDomainMessage("conv-1");
        var message2 = CreateDomainMessage("conv-1");

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent());

        await _actor.ReceiveAsync(message1, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        // After the first message a per-thread channel exists for conv-1
        // and is mid-drain. Per #2076 / ADR-0030 §3 §44 the second
        // message appends to the existing channel without launching a
        // parallel dispatcher.
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + "conv-1", Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true,
                new ThreadChannel { ThreadId = "conv-1", Messages = [message1], Dispatching = true }));

        await _actor.ReceiveAsync(message2, TestContext.Current.CancellationToken);

        // Still only one dispatch — #133 kicks off a dispatch only when a conversation becomes active.
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchTerminal_DoesNotEmitDispatchResponseRecordedOnThread()
    {
        // Regression guard for the deleted synthesis row (ADR-0056 §9):
        // the legacy WorkflowStepCompleted "Dispatch response recorded
        // on thread." emission is gone on every terminal path.
        var message = CreateDomainMessage();
        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.WorkflowStepCompleted
                && (e.Summary ?? string.Empty).Contains("Dispatch response recorded on thread")),
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
