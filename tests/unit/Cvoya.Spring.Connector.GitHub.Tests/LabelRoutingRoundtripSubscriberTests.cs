// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Reactive.Subjects;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// Integration-style coverage for <see cref="LabelRoutingRoundtripSubscriber"/>
/// (#492). Uses a fake <see cref="IActivityEventBus"/> backed by a real
/// <see cref="Subject{T}"/> plus an NSubstitute <see cref="IGitHubClient"/> so
/// the Rx subscription wiring, event filtering, and Octokit call surface are
/// all exercised end-to-end.
/// </summary>
public class LabelRoutingRoundtripSubscriberTests
{
    private readonly FakeActivityEventBus _bus = new();
    private readonly IGitHubClient _client = Substitute.For<IGitHubClient>();
    private readonly IGitHubConnector _connector = Substitute.For<IGitHubConnector>();
    private readonly IUnitConnectorConfigStore _configStore = Substitute.For<IUnitConnectorConfigStore>();
    private readonly ILogger<LabelRoutingRoundtripSubscriber> _logger;
    private readonly LabelRoutingRoundtripSubscriber _subscriber;

    public LabelRoutingRoundtripSubscriberTests()
    {
        _logger = Substitute.For<ILogger<LabelRoutingRoundtripSubscriber>>();
        _connector.CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>())
            .Returns(_client);
        // Per-binding overload added in #2385 — return the same fake client
        // for any installation id so binding-aware tests can assert which
        // overload was called without losing the happy-path behaviour.
        _connector.CreateAuthenticatedClientAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_client);
        _subscriber = new LabelRoutingRoundtripSubscriber(_bus, _connector, _configStore, _logger);
    }

    [Fact]
    public async Task Start_AppliesAddAndRemove_OnRoutedDelegateDecision()
    {
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: new[] { "in-progress" },
            RemoveOnAssign: new[] { "agent:backend" }));
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(unit, number: 42));

        // The hosted-service's OnNext is fire-and-forget; wait for the
        // per-label calls to appear rather than assuming a single drain.
        await WaitForAsync(() =>
            _client.Issue.Labels.ReceivedCalls().Count() >= 2);

        await _client.Issue.Labels.Received(1)
            .RemoveFromIssue("acme", "widgets", 42, "agent:backend");
        await _client.Issue.Labels.Received(1)
            .AddToIssue("acme", "widgets", 42,
                Arg.Is<string[]>(l => l.Length == 1 && l[0] == "in-progress"));

        await _subscriber.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Apply_BindingInstallationId_DrivesAuth()
    {
        // Per-binding installation ids drive label-roundtrip auth (#2385).
        // When AppInstallationId is set on the binding, the subscriber MUST
        // call the connector's (long, CancellationToken) overload instead of
        // falling through to the global-default path.
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AppInstallationId: 4242,
            AddOnAssign: new[] { "in-progress" }));

        await _subscriber.ApplyRoundtripAsync(BuildEvent(unit, number: 42),
            TestContext.Current.CancellationToken);

        await _connector.Received(1)
            .CreateAuthenticatedClientAsync(4242, Arg.Any<CancellationToken>());
        await _connector.DidNotReceive()
            .CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_BindingWithoutInstallationId_FallsBackToGlobal()
    {
        // null AppInstallationId is the documented fallback for OSS
        // deployments that never bound a per-unit installation (#2385).
        // The subscriber must call the parameterless overload so the
        // connector's global default kicks in.
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: new[] { "in-progress" }));

        await _subscriber.ApplyRoundtripAsync(BuildEvent(unit, number: 42),
            TestContext.Current.CancellationToken);

        await _connector.Received(1)
            .CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>());
        await _connector.DidNotReceive()
            .CreateAuthenticatedClientAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_IgnoresNonDelegateDecision()
    {
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: new[] { "in-progress" }));
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(
            unit,
            number: 42,
            kind: OrchestrationDecisionKind.Fanout));

        await Task.Delay(20, TestContext.Current.CancellationToken);
        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();

        await _subscriber.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_IgnoresUnitWithoutGitHubBinding()
    {
        var unit = UnitAddress();
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(unit, number: 42));

        await Task.Delay(20, TestContext.Current.CancellationToken);
        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();

        await _subscriber.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Apply_RemoveMissingLabel_TreatedAsNoOp()
    {
        _client.Issue.Labels
            .RemoveFromIssue("acme", "widgets", 42, "stale")
            .Throws(new NotFoundException(
                "Label not found", HttpStatusCode.NotFound));

        // Direct apply path — avoids Rx fire-and-forget timing in this
        // specific idempotency check.
        await _subscriber.ApplyWithClientAsync(
            _client, "acme", "widgets", 42,
            addList: new[] { "in-progress" },
            removeList: new[] { "stale" },
            TestContext.Current.CancellationToken);

        await _client.Issue.Labels.Received(1)
            .RemoveFromIssue("acme", "widgets", 42, "stale");
        await _client.Issue.Labels.Received(1)
            .AddToIssue("acme", "widgets", 42,
                Arg.Is<string[]>(l => l.Length == 1 && l[0] == "in-progress"));
    }

    [Fact]
    public async Task Apply_AdditionReturns404_SwallowedGracefully()
    {
        _client.Issue.Labels
            .AddToIssue("acme", "widgets", 42, Arg.Any<string[]>())
            .Throws(new NotFoundException(
                "Issue not found", HttpStatusCode.NotFound));

        await Should.NotThrowAsync(() => _subscriber.ApplyWithClientAsync(
            _client, "acme", "widgets", 42,
            addList: new[] { "in-progress" },
            removeList: Array.Empty<string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_PermissionDenied_AbortsRemainingCalls()
    {
        _client.Issue.Labels
            .RemoveFromIssue("acme", "widgets", 42, "agent:backend")
            .Throws(new ForbiddenException(
                new ResponseFake(HttpStatusCode.Forbidden)));

        await _subscriber.ApplyWithClientAsync(
            _client, "acme", "widgets", 42,
            addList: new[] { "in-progress" },
            removeList: new[] { "agent:backend" },
            TestContext.Current.CancellationToken);

        // Permission failure on the remove aborts the roundtrip — the add
        // must NOT fire because we cannot trust the auth surface on a
        // subsequent call either.
        await _client.Issue.Labels.DidNotReceiveWithAnyArgs()
            .AddToIssue(default!, default!, default, default!);
    }

    [Fact]
    public async Task Apply_NoLabels_NoCalls()
    {
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: Array.Empty<string>(),
            RemoveOnAssign: Array.Empty<string>()));

        await _subscriber.ApplyRoundtripAsync(BuildEvent(unit, number: 42),
            TestContext.Current.CancellationToken);

        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();
        await _connector.DidNotReceive().CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_MissingIssueContext_Skipped()
    {
        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: new[] { "in-progress" }));
        var evt = BuildEvent(unit, number: 42, includeIssueMetadata: false);

        await _subscriber.ApplyRoundtripAsync(evt, TestContext.Current.CancellationToken);

        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void Filter_AcceptsOnlyRoutedDelegateDecisionEvents()
    {
        var unit = UnitAddress();
        var good = BuildEvent(unit, number: 1);
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(good)
            .ShouldBeTrue();

        var nonDecision = good with { EventType = ActivityEventType.MessageReceived };
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(nonDecision)
            .ShouldBeFalse();

        var nullDetails = good with { Details = null };
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(nullDetails)
            .ShouldBeFalse();

        var fanout = BuildEvent(
            unit,
            number: 1,
            kind: OrchestrationDecisionKind.Fanout);
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(fanout)
            .ShouldBeFalse();

        var failed = BuildEvent(
            unit,
            number: 1,
            status: OrchestrationDecisionStatus.Failed);
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(failed)
            .ShouldBeFalse();
    }

    [Fact]
    public void Filter_ReturnsFalse_WhenEventIsNull()
    {
        LabelRoutingRoundtripSubscriber.IsRoutedDelegateDecision(null!)
            .ShouldBeFalse();
    }

    [Fact]
    public void TryExtractIssueNumber_ReturnsFalseOnMissingIssue()
    {
        var metadata = JsonSerializer.SerializeToElement(new
        {
            repository = new { owner = "acme", name = "widgets" },
        });
        LabelRoutingRoundtripSubscriber.TryExtractIssueNumber(metadata, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_DrainsInFlightHandlers_BeforeReturn()
    {
        // Regression: fire-and-forget handlers must not leak past StopAsync,
        // otherwise their late auth / Octokit calls fault the host teardown
        // on unrelated tests sharing the WebApplicationFactory. This was the
        // root cause of the class-cleanup gRPC failures seen on PR #507 CI
        // runs in `UnitDeleteEndpointTests`.
        var gate = new TaskCompletionSource();
        var handlerFinished = new TaskCompletionSource();

        _connector.CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                try { await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
                handlerFinished.TrySetResult();
                return _client;
            });

        var unit = UnitAddress();
        RegisterConfig(unit, new UnitGitHubConfig(
            "acme",
            "widgets",
            AddOnAssign: new[] { "in-progress" }));

        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(unit, number: 42));

        // Wait for the handler to have entered the auth call — it's now in-flight.
        await WaitForAsync(() =>
            _connector.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "CreateAuthenticatedClientAsync"));

        // Kick off StopAsync. It must not return until the handler finishes.
        var stopTask = _subscriber.StopAsync(TestContext.Current.CancellationToken);

        // Cancellation should have propagated to the handler — release the gate
        // by signalling completion directly (simulates the handler observing
        // its token firing). In production this is what lets the handler
        // exit quickly; the drain loop caps the wait at DrainTimeout anyway.
        gate.TrySetResult();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        handlerFinished.Task.IsCompleted.ShouldBeTrue(
            "StopAsync must not return before the in-flight handler completes");
    }

    private void RegisterConfig(Address unit, UnitGitHubConfig config)
    {
        var stored = JsonSerializer.SerializeToElement(
            config,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _configStore.GetAsync(unit.Path, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));
    }

    private static ActivityEvent BuildEvent(
        Address unit,
        int number,
        OrchestrationDecisionKind kind = OrchestrationDecisionKind.Delegate,
        OrchestrationDecisionStatus status = OrchestrationDecisionStatus.Routed,
        bool includeIssueMetadata = true)
    {
        JsonElement? metadata = includeIssueMetadata
            ? JsonSerializer.SerializeToElement(new { issue = new { number } })
            : null;
        var decision = new OrchestrationDecision(
            Guid.NewGuid(),
            Guid.Empty,
            unit,
            Guid.NewGuid(),
            Guid.NewGuid(),
            kind,
            new[] { Address.For("agent", TestSlugIds.HexFor("backend-engineer")) },
            status,
            status == OrchestrationDecisionStatus.Routed
                ? new[] { Guid.NewGuid() }
                : Array.Empty<Guid>(),
            Reason: null,
            metadata,
            DateTimeOffset.UtcNow);
        var details = JsonSerializer.SerializeToElement(decision);
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            unit,
            ActivityEventType.DecisionMade,
            ActivitySeverity.Info,
            "delegated assignment",
            details,
            CorrelationId: Guid.NewGuid().ToString());
    }

    private static Address UnitAddress() =>
        Address.For("unit", TestSlugIds.HexFor("engineering-team"));

    /// <summary>
    /// Spin-wait with a ceiling so Rx's fire-and-forget handler has time to
    /// land without coupling to wall-clock sleeps. 2 seconds is generous
    /// relative to the no-op path's actual latency (microseconds) but stays
    /// well under the xunit test timeout.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        condition().ShouldBeTrue("condition was not satisfied within timeout");
    }

    /// <summary>
    /// Real <see cref="IActivityEventBus"/> using an Rx <see cref="Subject{T}"/>.
    /// The production implementation in <c>Cvoya.Spring.Dapr</c> uses the same
    /// primitive — we don't reuse it here to avoid a cross-project test
    /// dependency. Keeps this test suite strictly scoped to the connector
    /// package.
    /// </summary>
    private sealed class FakeActivityEventBus : IActivityEventBus
    {
        private readonly Subject<ActivityEvent> _subject = new();

        public IObservable<ActivityEvent> ActivityStream => _subject;

        public void Publish(ActivityEvent evt) => _subject.OnNext(evt);

        public Task PublishAsync(ActivityEvent evt, CancellationToken cancellationToken = default)
        {
            _subject.OnNext(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal fake of Octokit's <see cref="IResponse"/> so we can build an
    /// <see cref="ApiException"/> without hitting the real HTTP stack.
    /// </summary>
    private sealed class ResponseFake(HttpStatusCode statusCode) : IResponse
    {
        public object Body => string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; }
            = new Dictionary<string, string>();

        public ApiInfo ApiInfo { get; } = new ApiInfo(
            new Dictionary<string, Uri>(),
            new List<string>(),
            new List<string>(),
            "etag",
            new Octokit.RateLimit(1, 1, 1));

        public HttpStatusCode StatusCode { get; } = statusCode;

        public string ContentType { get; } = "application/json";
    }
}
