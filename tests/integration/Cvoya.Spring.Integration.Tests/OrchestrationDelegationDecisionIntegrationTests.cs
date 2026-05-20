// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    private static readonly Guid TenantId = OssTenantIds.Default;

    private static readonly Address ChildOne =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001828"));

    private static readonly Address ChildTwo =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001829"));

    private static readonly Address ChildThree =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001830"));

    [Fact]
    public async Task HandleDelegateTo_TargetIsMember_EmitsRoutedDecisionEvent()
    {
        // Arrange
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo);
        var child = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var threadId = Guid.NewGuid();

        harness.RegisterAgent(ChildOne, child);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        // Act
        var ack = await harness.Handlers.HandleDelegateToAsync(
            ParentUnit,
            TenantId,
            ChildOne,
            message,
            reason: "route to the specialist",
            threadId,
            TestContext.Current.CancellationToken);

        // Assert — ADR-0049: delegate_to returns a delivery acknowledgement.
        ack.Delivered.ShouldBeTrue();
        ack.Target.ShouldBe(ChildOne);
        ack.MessageId.ShouldBe(message.Id);
        var decision = harness.ReadSingleDecision();
        AssertDecisionTenant(decision);
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Targets.ShouldBe([ChildOne]);
        decision.ResultMessageIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleDelegateTo_DeliveryFails_EmitsFailedDecisionEventWithTenant()
    {
        // Arrange — a persistent transient ReceiveAsync failure (ADR-0049 §6).
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo);
        var child = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var threadId = Guid.NewGuid();
        const string failureDescription = "child unreachable";

        harness.RegisterAgent(ChildOne, child);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(failureDescription));

        // Act — terminal delivery failure surfaces as OrchestrationException.
        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            harness.Handlers.HandleDelegateToAsync(
                ParentUnit,
                TenantId,
                ChildOne,
                message,
                reason: failureDescription,
                threadId,
                TestContext.Current.CancellationToken));
        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationDeliveryFailed);

        // Assert
        var decision = harness.ReadSingleDecision();
        AssertDecisionTenant(decision);
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.Targets.ShouldBe([ChildOne]);
        decision.ResultMessageIds.ShouldBeEmpty();
        decision.Reason.ShouldBe(failureDescription);
    }

    [Fact]
    public async Task HandleFanoutTo_AllTargetsSucceed_EmitsRoutedDecisionEvent()
    {
        // Arrange
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo, ChildThree);
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var threadId = Guid.NewGuid();

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        // Act
        var outcomes = await harness.Handlers.HandleFanoutToAsync(
            ParentUnit,
            TenantId,
            [ChildOne, ChildTwo, ChildThree],
            message,
            reason: "ask all children",
            threadId,
            TestContext.Current.CancellationToken);

        // Assert — ADR-0049: per-target delivery outcomes, not work products.
        outcomes.Select(outcome => outcome.Target)
            .ShouldBe([ChildOne, ChildTwo, ChildThree]);
        outcomes.All(outcome => outcome.Delivered).ShouldBeTrue();

        var decision = harness.ReadSingleDecision();
        AssertDecisionTenant(decision);
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Targets.ShouldBe([ChildOne, ChildTwo, ChildThree]);
        decision.ResultMessageIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleFanoutTo_OneTargetDeliveryFails_EmitsFailedDecisionEvent()
    {
        // Arrange — one target's mailbox enqueue fails persistently.
        var harness = CreateHarness(ParentUnit, ChildOne, ChildTwo, ChildThree);
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var threadId = Guid.NewGuid();
        const string failureDescription = "child two unreachable";

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(failureDescription));
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        // Act
        var outcomes = await harness.Handlers.HandleFanoutToAsync(
            ParentUnit,
            TenantId,
            [ChildOne, ChildTwo, ChildThree],
            message,
            reason: failureDescription,
            threadId,
            TestContext.Current.CancellationToken);

        // Assert
        outcomes.Count.ShouldBe(3);
        outcomes[0].Delivered.ShouldBeTrue();
        outcomes[1].Target.ShouldBe(ChildTwo);
        outcomes[1].Delivered.ShouldBeFalse();
        outcomes[1].Error.ShouldNotBeNull();
        outcomes[2].Delivered.ShouldBeTrue();

        var decision = harness.ReadSingleDecision();
        AssertDecisionTenant(decision);
        decision.UnitAddress.ShouldBe(ParentUnit);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.Targets.ShouldBe([ChildOne, ChildTwo, ChildThree]);
        decision.ResultMessageIds.ShouldBeEmpty();
        decision.Reason.ShouldNotBeNull();
        decision.Reason.ShouldContain(failureDescription);
    }

    private static HandlerHarness CreateHarness(Address parent, params Address[] children)
    {
        var agents = new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase);
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
        var activityEventBus = Substitute.For<IActivityEventBus>();
        var publishedEvents = new List<ActivityEvent>();

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

        // ADR-0049 — tighten the delivery retry budget so the
        // terminal-failure path exhausts in milliseconds under test.
        var deliveryOptions = Options.Create(new OrchestrationDeliveryOptions
        {
            MaxAttempts = 3,
            Budget = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var handlers = new OrchestrationToolHandlers(
            agentProxyResolver,
            Substitute.For<ILogger<OrchestrationToolHandlers>>(),
            activityEventBus,
            new SingleTenantOrchestrationTenantResolver(),
            deliveryOptions);

        return new HandlerHarness(handlers, agents, publishedEvents);
    }

    private static void AssertDecisionTenant(OrchestrationDecision decision)
    {
        decision.TenantId.ShouldBe(TenantId);
        decision.TenantId.ShouldNotBe(Guid.Empty);
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
