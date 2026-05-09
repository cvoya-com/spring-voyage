// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the GitHub label roundtrip after ADR-0039:
/// an orchestration delegate tool call emits an <see cref="OrchestrationDecision"/>
/// activity event, and the GitHub connector subscriber applies the binding's
/// label rules through Octokit.
/// </summary>
public class GitHubLabelRoutingRoundtrip
{
    private static readonly Address Unit =
        new(Address.UnitScheme, new Guid("bbbbbbbb-0000-0000-0000-000000001859"));

    private static readonly Address Child =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001859"));

    [Fact]
    public async Task DelegateDecision_WithGitHubBinding_AppliesConfiguredLabelRules()
    {
        var bus = new RecordingActivityEventBus();
        var client = Substitute.For<IGitHubClient>();
        var connector = Substitute.For<IGitHubConnector>();
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var subscriber = new LabelRoutingRoundtripSubscriber(
            bus,
            connector,
            configStore,
            Substitute.For<ILogger<LabelRoutingRoundtripSubscriber>>());
        var harness = CreateHandlerHarness(bus);
        var child = Substitute.For<IAgent>();
        var message = CreateGitHubIssueMessage(issueNumber: 314);
        var response = CreateResponse();
        var threadId = Guid.NewGuid();

        connector.CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>())
            .Returns(client);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(response);
        harness.RegisterAgent(Child, child);
        RegisterConfig(configStore, new UnitGitHubConfig(
            "acme",
            "platform",
            AddOnAssign: new[] { "triage" },
            RemoveOnAssign: new[] { "needs-assignment" }));

        await subscriber.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await harness.Handlers.HandleDelegateToChildAsync(
                Unit,
                OssTenantIds.Default,
                Child,
                message,
                reason: "route issue to implementation agent",
                threadId,
                TestContext.Current.CancellationToken);

            await WaitForAsync(() =>
                client.Issue.Labels.ReceivedCalls().Count() >= 2);
        }
        finally
        {
            await subscriber.StopAsync(TestContext.Current.CancellationToken);
        }

        var activityEvent = bus.PublishedEvents.Single();
        activityEvent.EventType.ShouldBe(ActivityEventType.DecisionMade);
        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(
            activityEvent.Details!.Value.GetRawText());
        decision.ShouldNotBeNull();
        decision!.TenantId.ShouldBe(OssTenantIds.Default);
        decision.TenantId.ShouldNotBe(Guid.Empty);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);

        client.Issue.Labels.ReceivedCalls().Count().ShouldBe(2);
        await client.Issue.Labels.Received(1)
            .AddToIssue("acme", "platform", 314,
                Arg.Is<string[]>(labels => labels.SequenceEqual(new[] { "triage" })));
        await client.Issue.Labels.Received(1)
            .RemoveFromIssue("acme", "platform", 314, "needs-assignment");
    }

    private static HandlerHarness CreateHandlerHarness(RecordingActivityEventBus bus)
    {
        var (unitActor, stateManager, _) =
            ActorTestHost.CreateUnitActor(actorId: GuidFormatter.Format(Unit.Id));

        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [Child]));

        var agents = new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase);
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        actorProxyFactory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(actorId => actorId.GetId() == GuidFormatter.Format(Unit.Id)),
                nameof(UnitActor))
            .Returns(unitActor);

        agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                var scheme = call.ArgAt<string>(0);
                var actorId = call.ArgAt<string>(1);
                return agents.TryGetValue($"{scheme}:{actorId}", out var agent)
                    ? agent
                    : null;
            });

        var handlers = new OrchestrationToolHandlers(
            actorProxyFactory,
            agentProxyResolver,
            new OrchestrationDepthCounter(),
            Substitute.For<ILogger<OrchestrationToolHandlers>>(),
            bus,
            new SingleTenantOrchestrationTenantResolver());

        return new HandlerHarness(handlers, agents);
    }

    private static void RegisterConfig(
        IUnitConnectorConfigStore configStore,
        UnitGitHubConfig config)
    {
        var stored = JsonSerializer.SerializeToElement(
            config,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        configStore.GetAsync(Unit.Path, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));
    }

    private static Message CreateGitHubIssueMessage(int issueNumber) =>
        new(
            Guid.NewGuid(),
            new Address(Address.HumanScheme, new Guid("cccccccc-0000-0000-0000-000000001859")),
            Unit,
            MessageType.Domain,
            Guid.NewGuid().ToString("D"),
            JsonSerializer.SerializeToElement(new
            {
                source = "github",
                issue = new { number = issueNumber },
            }),
            DateTimeOffset.UtcNow);

    private static Message CreateResponse() =>
        new(
            Guid.NewGuid(),
            Child,
            Unit,
            MessageType.Domain,
            Guid.NewGuid().ToString("D"),
            JsonSerializer.SerializeToElement(new { content = "accepted" }),
            DateTimeOffset.UtcNow);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("condition was not satisfied within timeout");
    }

    private sealed record HandlerHarness(
        OrchestrationToolHandlers Handlers,
        Dictionary<string, IAgent> Agents)
    {
        public void RegisterAgent(Address address, IAgent agent) =>
            Agents[$"{address.Scheme}:{GuidFormatter.Format(address.Id)}"] = agent;
    }

    private sealed class RecordingActivityEventBus : IActivityEventBus, IObservable<ActivityEvent>
    {
        private readonly object _gate = new();
        private readonly List<IObserver<ActivityEvent>> _observers = new();

        public List<ActivityEvent> PublishedEvents { get; } = [];

        public IObservable<ActivityEvent> ActivityStream => this;

        public Task PublishAsync(ActivityEvent evt, CancellationToken cancellationToken = default)
        {
            IObserver<ActivityEvent>[] observers;
            lock (_gate)
            {
                PublishedEvents.Add(evt);
                observers = _observers.ToArray();
            }

            foreach (var observer in observers)
            {
                observer.OnNext(evt);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(IObserver<ActivityEvent> observer)
        {
            lock (_gate)
            {
                _observers.Add(observer);
            }

            return new Subscription(() =>
            {
                lock (_gate)
                {
                    _observers.Remove(observer);
                }
            });
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
