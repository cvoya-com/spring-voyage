// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core;
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
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests that verify <see cref="AgentActor.HandleDomainMessageAsync"/> invokes
/// <see cref="IExecutionDispatcher"/> and routes its response via
/// <see cref="MessageRouter"/> for the end-to-end dispatch path introduced by issue #133.
/// </summary>
public class AgentActorDispatchTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IExecutionDispatcher _dispatcher = Substitute.For<IExecutionDispatcher>();
    private readonly MessageRouter _router;
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly ISkillRegistry _skillRegistry = Substitute.For<ISkillRegistry>();
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly AgentActor _actor;

    public AgentActorDispatchTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());

        _skillRegistry.Name.Returns("github");
        _skillRegistry.GetToolDefinitions().Returns([
            new ToolDefinition("github.comment", "comment", JsonSerializer.SerializeToElement(new { }))
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
            new AgentDispatchCoordinator(_dispatcher, _router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
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
            .Returns((Message?)null);

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
    public async Task DispatchResponse_IsRoutedViaMessageRouter()
    {
        var message = CreateDomainMessage();
        var response = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            message.From,
            MessageType.Domain,
            message.ThreadId,
            JsonSerializer.SerializeToElement(new { Output = "ok", ExitCode = 0 }),
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(response);
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Success(null));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _router.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.Id == response.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RoutingFailure_EmitsErrorActivity()
    {
        var message = CreateDomainMessage();
        var response = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            message.From,
            MessageType.Domain,
            message.ThreadId,
            JsonSerializer.SerializeToElement(new { Output = "ok", ExitCode = 0 }),
            DateTimeOffset.UtcNow);

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(response);

        // Routing fails: the dispatcher response is dropped. The coordinator
        // must surface this as an ErrorOccurred activity so the dropped
        // response is traceable in the portal activity feed (#2547).
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.AddressNotFound(message.From)));

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ErrorOccurred &&
                e.Severity == ActivitySeverity.Error &&
                e.Summary.Contains("ADDRESS_NOT_FOUND")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatcherReturnsNull_NoRoutingPerformed()
    {
        var message = CreateDomainMessage();

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);
        await _actor.PendingDispatchTask!;

        await _router.DidNotReceive().RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatcherThrows_ErrorIsLoggedNotPropagated()
    {
        var message = CreateDomainMessage();

        _dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException("dispatcher failed"));

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
            .Returns((Message?)null);

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
