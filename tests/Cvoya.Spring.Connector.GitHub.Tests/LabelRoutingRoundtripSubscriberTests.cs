// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Reactive.Subjects;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

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
    private readonly ILogger<LabelRoutingRoundtripSubscriber> _logger;
    private readonly LabelRoutingRoundtripSubscriber _subscriber;

    public LabelRoutingRoundtripSubscriberTests()
    {
        _logger = Substitute.For<ILogger<LabelRoutingRoundtripSubscriber>>();
        _connector.CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>())
            .Returns(_client);
        _subscriber = new LabelRoutingRoundtripSubscriber(_bus, _connector, _logger);
    }

    [Fact]
    public async Task Start_AppliesAddAndRemove_OnLabelRoutedGitHubEvent()
    {
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(
            owner: "acme",
            repo: "widgets",
            number: 42,
            addOnAssign: new[] { "in-progress" },
            removeOnAssign: new[] { "agent:backend" }));

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
    public async Task Start_IgnoresEventWithoutLabelRoutedDecision()
    {
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        var details = JsonSerializer.SerializeToElement(new
        {
            decision = "DelegateToStrategy", // not a label-routed decision
            source = "github",
            repository = new { owner = "acme", name = "widgets" },
            issue = new { number = 42 },
            addOnAssign = new[] { "in-progress" },
            removeOnAssign = Array.Empty<string>(),
        });
        _bus.Publish(new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("unit", TestSlugIds.HexFor("team")),
            ActivityEventType.DecisionMade, ActivitySeverity.Info,
            "some other decision", details));

        await Task.Delay(20, TestContext.Current.CancellationToken);
        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();

        await _subscriber.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_IgnoresNonGitHubLabelRoutedEvent()
    {
        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        var details = JsonSerializer.SerializeToElement(new
        {
            decision = "LabelRouted",
            source = "linear", // not github
            repository = new { owner = "acme", name = "widgets" },
            issue = new { number = 42 },
            addOnAssign = new[] { "in-progress" },
            removeOnAssign = Array.Empty<string>(),
        });
        _bus.Publish(new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("unit", TestSlugIds.HexFor("team")),
            ActivityEventType.DecisionMade, ActivitySeverity.Info,
            "linear assignment", details));

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
        await _subscriber.ApplyRoundtripAsync(BuildEvent(
            owner: "acme", repo: "widgets", number: 42,
            addOnAssign: Array.Empty<string>(),
            removeOnAssign: Array.Empty<string>()),
            TestContext.Current.CancellationToken);

        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();
        await _connector.DidNotReceive().CreateAuthenticatedClientAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_MissingRepositoryContext_Skipped()
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            decision = "LabelRouted",
            source = "github",
            addOnAssign = new[] { "in-progress" },
            removeOnAssign = Array.Empty<string>(),
            // no repository / issue fields
        });
        var evt = new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("unit", TestSlugIds.HexFor("team")),
            ActivityEventType.DecisionMade, ActivitySeverity.Info,
            "malformed label-routed event", details);

        await _subscriber.ApplyRoundtripAsync(evt, TestContext.Current.CancellationToken);

        _client.Issue.Labels.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public void Filter_AcceptsOnlyLabelRoutedGitHubEvents()
    {
        var good = BuildEvent("a", "b", 1, new[] { "x" }, Array.Empty<string>());
        LabelRoutingRoundtripSubscriber.IsLabelRoutedGitHubAssignment(good)
            .ShouldBeTrue();

        var nonDecision = good with { EventType = ActivityEventType.MessageReceived };
        LabelRoutingRoundtripSubscriber.IsLabelRoutedGitHubAssignment(nonDecision)
            .ShouldBeFalse();

        var nullDetails = good with { Details = null };
        LabelRoutingRoundtripSubscriber.IsLabelRoutedGitHubAssignment(nullDetails)
            .ShouldBeFalse();
    }

    [Fact]
    public void Filter_ReturnsFalse_WhenEventIsNull()
    {
        LabelRoutingRoundtripSubscriber.IsLabelRoutedGitHubAssignment(null!)
            .ShouldBeFalse();
    }

    [Fact]
    public void TryExtractTarget_ReturnsFalseOnMissingIssue()
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            decision = "LabelRouted",
            source = "github",
            repository = new { owner = "acme", name = "widgets" },
        });
        LabelRoutingRoundtripSubscriber.TryExtractTarget(
            details, out _, out _, out _, out _, out _)
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

        await _subscriber.StartAsync(TestContext.Current.CancellationToken);

        _bus.Publish(BuildEvent(
            owner: "acme",
            repo: "widgets",
            number: 42,
            addOnAssign: new[] { "in-progress" },
            removeOnAssign: Array.Empty<string>()));

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

    private static ActivityEvent BuildEvent(
        string owner,
        string repo,
        int number,
        IReadOnlyList<string> addOnAssign,
        IReadOnlyList<string> removeOnAssign)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            decision = "LabelRouted",
            unitAddress = new { scheme = "unit", path = "engineering-team" },
            matchedLabel = "agent:backend",
            target = new { scheme = "agent", path = "backend-engineer" },
            source = "github",
            repository = new { owner, name = repo },
            issue = new { number },
            addOnAssign,
            removeOnAssign,
            messageId = Guid.NewGuid(),
        });
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("unit", TestSlugIds.HexFor("engineering-team")),
            ActivityEventType.DecisionMade,
            ActivitySeverity.Info,
            "label-routed assignment",
            details,
            CorrelationId: Guid.NewGuid().ToString());
    }

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