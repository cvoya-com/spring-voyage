// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration smoke tests for ADR-0039 orchestration delegation decisions.
/// The parent membership source is a real UnitActor created through
/// ActorTestHost; child mailboxes and the activity bus are mocked boundaries.
/// </summary>
public class OrchestrationDelegationDecisionIntegrationTests
{
    private static readonly Address ParentUnit =
        new(Address.UnitScheme, new Guid("bbbbbbbb-0000-0000-0000-000000001828"));

    private static readonly Address ChildOne =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001828"));

    private static readonly Address ChildTwo =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001829"));

    private static readonly Address ChildThree =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001830"));

    [Fact]
    public async Task HandleDelegateToChild_DirectChild_EmitsRoutedDecisionEvent()
    {
        // Arrange
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo);
        var child = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var response = CreateResponse(ChildOne, ParentUnit, "child response");
        var threadId = Guid.NewGuid();

        harness.RegisterAgent(ChildOne, child);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(response);

        // Act
        var result = await harness.Handlers.HandleDelegateToChildAsync(
            ParentUnit,
            ChildOne,
            message,
            reason: "route to the specialist",
            threadId,
            TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(response);
        var decision = harness.ReadSingleDecision();
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Targets.ShouldBe([ChildOne]);
        decision.ResultMessageIds.ShouldBe([response.Id]);
    }

    [Fact]
    public async Task HandleFanoutToChildren_AllChildrenSucceed_EmitsRoutedDecisionEvent()
    {
        // Arrange
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo, ChildThree);
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var responseOne = CreateResponse(ChildOne, ParentUnit, "first response");
        var responseTwo = CreateResponse(ChildTwo, ParentUnit, "second response");
        var responseThree = CreateResponse(ChildThree, ParentUnit, "third response");
        var threadId = Guid.NewGuid();

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseOne);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseTwo);
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseThree);

        // Act
        var results = await harness.Handlers.HandleFanoutToChildrenAsync(
            ParentUnit,
            [ChildOne, ChildTwo, ChildThree],
            message,
            reason: "ask all children",
            threadId,
            TestContext.Current.CancellationToken);

        // Assert
        results.Select(result => result.Response?.Id)
            .ShouldBe([responseOne.Id, responseTwo.Id, responseThree.Id]);

        var decision = harness.ReadSingleDecision();
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Targets.ShouldBe([ChildOne, ChildTwo, ChildThree]);
        decision.ResultMessageIds.ShouldBe([responseOne.Id, responseTwo.Id, responseThree.Id]);
    }

    [Fact]
    public async Task HandleFanoutToChildren_OneChildErrors_EmitsFailedDecisionEvent()
    {
        // Arrange
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo, ChildThree);
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var responseOne = CreateResponse(ChildOne, ParentUnit, "first response");
        var responseThree = CreateResponse(ChildThree, ParentUnit, "third response");
        var threadId = Guid.NewGuid();
        const string failureDescription = "child two failed";

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseOne);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(failureDescription));
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseThree);

        // Act
        var results = await harness.Handlers.HandleFanoutToChildrenAsync(
            ParentUnit,
            [ChildOne, ChildTwo, ChildThree],
            message,
            reason: failureDescription,
            threadId,
            TestContext.Current.CancellationToken);

        // Assert
        results.Length.ShouldBe(3);
        results[1].Target.ShouldBe(ChildTwo);
        results[1].Response.ShouldBeNull();
        results[1].Error.ShouldBeOfType<InvalidOperationException>();

        var decision = harness.ReadSingleDecision();
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.Targets.ShouldBe([ChildOne, ChildTwo, ChildThree]);
        decision.ResultMessageIds.ShouldBe([responseOne.Id, responseThree.Id]);
        decision.Reason.ShouldNotBeNull();
        decision.Reason.ShouldContain(failureDescription);
    }

    private static HandlerHarness CreateHarness(Address parent, params Address[] children)
    {
        var (unitActor, stateManager, _) =
            ActorTestHost.CreateUnitActor(actorId: GuidFormatter.Format(parent.Id));

        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, children.ToList()));

        var agents = new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase);
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
        var activityEventBus = Substitute.For<IActivityEventBus>();
        var publishedEvents = new List<ActivityEvent>();

        actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(actorId => actorId.GetId() == GuidFormatter.Format(parent.Id)),
                nameof(UnitActor))
            .Returns(unitActor);

        agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                var scheme = call.ArgAt<string>(0);
                var actorId = call.ArgAt<string>(1);
                return agents.TryGetValue(Key(scheme, actorId), out var agent)
                    ? agent
                    : null;
            });

        activityEventBus
            .PublishAsync(
                Arg.Do<ActivityEvent>(activityEvent => publishedEvents.Add(activityEvent)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handlers = new OrchestrationToolHandlers(
            actorProxyFactory,
            agentProxyResolver,
            new OrchestrationDepthCounter(),
            Substitute.For<ILogger<OrchestrationToolHandlers>>(),
            activityEventBus);

        return new HandlerHarness(handlers, agents, publishedEvents);
    }

    private static string Key(Address address) => Key(address.Scheme, GuidFormatter.Format(address.Id));

    private static string Key(string scheme, string id) => $"{scheme}:{id}";

    private static Message CreateMessage(Address to) =>
        new(
            Guid.NewGuid(),
            new Address(Address.HumanScheme, new Guid("cccccccc-0000-0000-0000-000000001828")),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString("D"),
            JsonSerializer.SerializeToElement(new { Content = "work" }),
            DateTimeOffset.UtcNow);

    private static Message CreateResponse(Address from, Address to, string content) =>
        new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString("D"),
            JsonSerializer.SerializeToElement(new { Content = content }),
            DateTimeOffset.UtcNow);

    private sealed record HandlerHarness(
        OrchestrationToolHandlers Handlers,
        Dictionary<string, IAgent> Agents,
        List<ActivityEvent> PublishedEvents)
    {
        public void RegisterAgent(Address address, IAgent agent) =>
            Agents[Key(address)] = agent;

        public OrchestrationDecision ReadSingleDecision()
        {
            PublishedEvents.Count.ShouldBe(1);
            var activityEvent = PublishedEvents.Single();

            activityEvent.Source.ShouldBe(ParentUnit);
            activityEvent.EventType.ShouldBe(ActivityEventType.DecisionMade);
            activityEvent.Details.ShouldNotBeNull();

            var decision = JsonSerializer.Deserialize<OrchestrationDecision>(
                activityEvent.Details!.Value.GetRawText());

            decision.ShouldNotBeNull();
            return decision!;
        }
    }
}
