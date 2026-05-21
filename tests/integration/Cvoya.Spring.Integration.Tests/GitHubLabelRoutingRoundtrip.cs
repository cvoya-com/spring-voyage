// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the GitHub label roundtrip: a routing
/// <see cref="OrchestrationDecision"/> activity event (post-ADR-0049 these are
/// recorded by <c>sv.runtime.report_decision</c>, not by the messaging
/// delivery tools) carries the originating GitHub issue number, and the
/// GitHub connector subscriber applies the binding's label rules through
/// Octokit.
/// </summary>
public class GitHubLabelRoutingRoundtrip
{
    private static readonly Address Unit =
        new(Address.UnitScheme, new Guid("bbbbbbbb-0000-0000-0000-000000001859"));

    private static readonly Address Child =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001859"));

    [Fact]
    public async Task RoutingDecision_WithGitHubBinding_AppliesConfiguredLabelRules()
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

        connector.CreateAuthenticatedClientForBindingAsync(
                Arg.Any<UnitGitHubConfig>(), Arg.Any<CancellationToken>())
            .Returns(client);
        // ADR-0047 §6: every outbound call hands the binding to the
        // connector's CreateAuthenticatedClientForBindingAsync; the
        // resolver behind it dispatches App vs PAT. This integration
        // uses the PAT path to pin the broader fan-out shape.
        RegisterConfig(configStore, new UnitGitHubConfig(
            "acme/platform",
            AppInstallationId: null,
            PatSecretName: "binding/test/github/pat",
            AddOnAssign: new[] { "triage" },
            RemoveOnAssign: new[] { "needs-assignment" }));

        await subscriber.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // A routed delegate decision naming a GitHub issue — the shape
            // the runtime emits via sv.runtime.report_decision.
            await bus.PublishAsync(
                CreateDecisionEvent(issueNumber: 314),
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
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);

        client.Issue.Labels.ReceivedCalls().Count().ShouldBe(2);
        await client.Issue.Labels.Received(1)
            .AddToIssue("acme", "platform", 314,
                Arg.Is<string[]>(labels => labels.SequenceEqual(new[] { "triage" })));
        await client.Issue.Labels.Received(1)
            .RemoveFromIssue("acme", "platform", 314, "needs-assignment");
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

    private static ActivityEvent CreateDecisionEvent(int issueNumber)
    {
        var decision = new OrchestrationDecision(
            DecisionId: Guid.NewGuid(),
            TenantId: OssTenantIds.Default,
            UnitAddress: Unit,
            ThreadId: Guid.NewGuid(),
            InputMessageId: Guid.NewGuid(),
            Kind: OrchestrationDecisionKind.Delegate,
            Targets: [Child],
            Status: OrchestrationDecisionStatus.Routed,
            ResultMessageIds: [],
            Reason: "route issue to implementation agent",
            Metadata: JsonSerializer.SerializeToElement(new
            {
                issue = new { number = issueNumber },
            }),
            CreatedAt: DateTimeOffset.UtcNow);

        return new ActivityEvent(
            Guid.NewGuid(),
            decision.CreatedAt,
            Unit,
            ActivityEventType.DecisionMade,
            ActivitySeverity.Info,
            $"Routing decision to '{Child}' recorded.",
            JsonSerializer.SerializeToElement(decision),
            decision.ThreadId.ToString("D"));
    }

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
