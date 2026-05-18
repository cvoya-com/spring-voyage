// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Higher-level tests for <see cref="GitHubWebhookHandler.ApplyInboundFilterAsync"/>
/// — issue #2407. Confirms that the handler loads the unit binding, applies
/// the per-binding filter, emits an audit ActivityEvent on drop, and falls
/// through cleanly when no binding / no filter exists.
/// </summary>
public class GitHubWebhookHandlerFilterTests
{
    private static readonly string TargetUnitHex = TestSlugIds.HexFor("eng-team");

    [Fact]
    public async Task ApplyInboundFilterAsync_NoBindingStoreWired_PassesThrough()
    {
        // No IUnitConnectorConfigStore — handler must behave exactly as before
        // and return the translated message unchanged.
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_NoBindingForUnit_PassesThrough()
    {
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_BindingIsNotGitHubShaped_PassesThrough()
    {
        var nonGitHubTypeId = Guid.NewGuid();
        var emptyConfig = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(nonGitHubTypeId, emptyConfig));
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_FilterPasses_ReturnsMessage()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludeLabels: new[] { "spring-voyage" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "spring-voyage" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await bus.DidNotReceive().PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_FilterDrops_ReturnsNullAndEmitsActivity()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludeLabels: new[] { "spring-voyage" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? publishedEvent = null;
        await bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => publishedEvent = e),
            Arg.Any<CancellationToken>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "bug" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await bus.Received(1).PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
        publishedEvent.ShouldNotBeNull();
        publishedEvent!.EventType.ShouldBe(ActivityEventType.ConnectorEventFiltered);
        publishedEvent.Source.Scheme.ShouldBe("unit");
        publishedEvent.Source.Path.ShouldBe(TargetUnitHex);
        publishedEvent.Details.ShouldNotBeNull();
        var details = publishedEvent.Details!.Value;
        details.GetProperty("connector").GetString().ShouldBe("github");
        details.GetProperty("event_type").GetString().ShouldBe("issues");
        details.GetProperty("filter_kind").GetString().ShouldBe("include_label");
        details.GetProperty("filter_value").GetString().ShouldBe("spring-voyage");
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_ExcludeLabelDrops_EmitsAuditWithMatchedLabel()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            ExcludeLabels: new[] { "wip" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e),
            Arg.Any<CancellationToken>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "wip", "feature" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        captured.ShouldNotBeNull();
        captured!.Details!.Value.GetProperty("filter_kind").GetString().ShouldBe("exclude_label");
        // ExcludeLabel reports the specific matched label.
        captured.Details!.Value.GetProperty("filter_value").GetString().ShouldBe("wip");
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_StorageError_PassesThroughUnfiltered()
    {
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated transient blip"));
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        // A transient lookup failure must not silently suppress events.
        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_IncludePathsUnset_DoesNotInvokeFetcher()
    {
        // Filter that doesn't depend on changed-files (author filter,
        // matching the PR's user). When IncludePaths isn't configured the
        // handler must skip the /pulls/{n}/files round-trip entirely.
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludeAuthors: new[] { "opener" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            filesFetcher: fetcher);
        var translated = BuildPullRequestMessage(prNumber: 10);

        var result = await handler.ApplyInboundFilterAsync(translated, "pull_request", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await fetcher.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_IncludePathsSetAndMatches_AllowsViaFetch()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludePaths: new[] { "docs/" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 42, null, Arg.Any<CancellationToken>())
            .Returns(new[] { "docs/foo.md", "src/Bar.cs" });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            filesFetcher: fetcher);
        var translated = BuildPullRequestMessage(prNumber: 42);

        var result = await handler.ApplyInboundFilterAsync(translated, "pull_request", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await fetcher.Received(1).FetchAsync("acme", "platform", 42, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_IncludePathsSetAndDoesNotMatch_DropsWithAudit()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludePaths: new[] { "docs/" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 42, null, Arg.Any<CancellationToken>())
            .Returns(new[] { "src/Bar.cs" });

        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e),
            Arg.Any<CancellationToken>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus,
            filesFetcher: fetcher);
        var translated = BuildPullRequestMessage(prNumber: 42);

        var result = await handler.ApplyInboundFilterAsync(translated, "pull_request", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        captured.ShouldNotBeNull();
        captured!.Details!.Value.GetProperty("filter_kind").GetString().ShouldBe("include_path");
        captured.Details!.Value.GetProperty("event_type").GetString().ShouldBe("pull_request");
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_IncludePathsSetButFetcherFails_PassesThroughUnfiltered()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludePaths: new[] { "docs/" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 42, null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)null);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            filesFetcher: fetcher);
        var translated = BuildPullRequestMessage(prNumber: 42);

        var result = await handler.ApplyInboundFilterAsync(translated, "pull_request", TestContext.Current.CancellationToken);

        // Fail open — a transient fetch failure must not silently suppress
        // the operator's subscription.
        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_IncludePathsSetButIssueShape_DoesNotInvokeFetcher()
    {
        // Pure issue events have no PR shape — fetcher must not be called.
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludePaths: new[] { "docs/" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            filesFetcher: fetcher);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await fetcher.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_PassesBindingInstallationIdToFetcher()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            AppInstallationId: 9988L,
            IncludePaths: new[] { "docs/" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 42, 9988L, Arg.Any<CancellationToken>())
            .Returns(new[] { "docs/x.md" });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            filesFetcher: fetcher);
        var translated = BuildPullRequestMessage(prNumber: 42);

        await handler.ApplyInboundFilterAsync(translated, "pull_request", TestContext.Current.CancellationToken);

        await fetcher.Received(1).FetchAsync("acme", "platform", 42, 9988L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_NonUnitDestination_PassesThrough()
    {
        // A translated message whose destination scheme is not "unit"
        // (sentinel placeholder from CreateMessage before the resolver
        // runs, or a future shape) must not trigger a binding lookup —
        // the filter is a no-op for non-unit destinations.
        var store = Substitute.For<IUnitConnectorConfigStore>();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessageWithDestination(
            new Address("system", new Guid("00000000-0000-0000-0000-726f75746572")));

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await store.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Message BuildTranslatedMessage(IReadOnlyList<string>? labels = null)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "github",
            intent = "work_assignment",
            action = "opened",
            issue = new
            {
                number = 42,
                title = "T",
                labels = labels ?? Array.Empty<string>(),
                author = "opener",
            },
        });
        return new Message(
            Id: Guid.NewGuid(),
            From: new Address("connector", new Guid("00000000-0000-0000-0000-006769746875")),
            To: new Address(Address.UnitScheme, new Guid(TargetUnitHex)),
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static Message BuildPullRequestMessage(int prNumber)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "github",
            intent = "review_request",
            action = "opened",
            repository = new
            {
                owner = "acme",
                name = "platform",
                full_name = "acme/platform",
            },
            pull_request = new
            {
                number = prNumber,
                title = "T",
                author = "opener",
            },
        });
        return new Message(
            Id: Guid.NewGuid(),
            From: new Address("connector", new Guid("00000000-0000-0000-0000-006769746875")),
            To: new Address(Address.UnitScheme, new Guid(TargetUnitHex)),
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static Message BuildTranslatedMessageWithDestination(Address to)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "github",
            action = "opened",
            issue = new { number = 1, title = "x", labels = Array.Empty<string>() },
        });
        return new Message(
            Id: Guid.NewGuid(),
            From: new Address("connector", new Guid("00000000-0000-0000-0000-006769746875")),
            To: to,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }
}
