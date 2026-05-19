// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Wires the full <see cref="GitHubConnector"/> → <see cref="GitHubWebhookHandler"/>
/// → <see cref="GitHubEventFilter"/> chain to verify the drop path returns
/// the same "no deliver" outcome as the existing event-type-not-handled
/// branch, and that the audit <see cref="ActivityEvent"/> surfaces with the
/// expected shape. Issue #2407.
/// </summary>
public class GitHubConnectorWebhookFilterIntegrationTests
{
    private const string Secret = "test-secret";

    private static readonly string TargetUnitHex =
        new Guid("ee1ee111-0000-0000-0000-feedfeedfeed").ToString("N");

    [Fact]
    public async Task HandleWebhookAsync_FilterDropsEvent_ReturnsIgnoredAndEmitsActivity()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            IncludeLabels: new[] { "spring-voyage" });
        var (connector, bus) = BuildConnector(config);

        const string payload = """
        {
            "action": "opened",
            "installation": { "id": 1 },
            "repository": {
                "name": "platform",
                "full_name": "acme/platform",
                "owner": { "login": "acme" }
            },
            "issue": {
                "number": 7,
                "title": "Hi",
                "body": "x",
                "labels": [{ "name": "bug" }],
                "user": { "login": "carol" }
            }
        }
        """;

        var result = await connector.HandleWebhookAsync(
            "issues",
            payload,
            Sign(payload, Secret),
            TestContext.Current.CancellationToken);

        // Drop must surface as Ignored — the same outcome the endpoint
        // already maps to HTTP 202 (no routing call follows).
        result.Outcome.ShouldBe(WebhookOutcome.Ignored);
        result.Message.ShouldBeNull();

        await bus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ConnectorEventFiltered),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_FilterPassesEvent_ReturnsTranslatedAndDoesNotEmit()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            IncludeLabels: new[] { "spring-voyage" });
        var (connector, bus) = BuildConnector(config);

        const string payload = """
        {
            "action": "opened",
            "installation": { "id": 1 },
            "repository": {
                "name": "platform",
                "full_name": "acme/platform",
                "owner": { "login": "acme" }
            },
            "issue": {
                "number": 7,
                "title": "Hi",
                "body": "x",
                "labels": [{ "name": "spring-voyage" }],
                "user": { "login": "carol" }
            }
        }
        """;

        var result = await connector.HandleWebhookAsync(
            "issues",
            payload,
            Sign(payload, Secret),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(WebhookOutcome.Translated);
        result.Message.ShouldNotBeNull();
        result.Message!.To.Path.ShouldBe(TargetUnitHex);

        await bus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_PullRequestWithIncludePaths_FetchesFilesAndDeliversOnMatch()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            IncludePaths: new[] { "docs/" });
        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 9, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "docs/feature.md", "src/Other.cs" });

        var (connector, bus) = BuildConnector(config, fetcher);

        const string payload = """
        {
            "action": "opened",
            "installation": { "id": 1 },
            "repository": {
                "name": "platform",
                "full_name": "acme/platform",
                "owner": { "login": "acme" }
            },
            "pull_request": {
                "number": 9,
                "title": "Docs update",
                "body": "x",
                "head": { "ref": "feature" },
                "base": { "ref": "main" },
                "user": { "login": "alice" }
            }
        }
        """;

        var result = await connector.HandleWebhookAsync(
            "pull_request",
            payload,
            Sign(payload, Secret),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(WebhookOutcome.Translated);
        result.Message.ShouldNotBeNull();
        await fetcher.Received(1).FetchAsync(
            "acme", "platform", 9, Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await bus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_PullRequestWithIncludePaths_DropsOnMiss_SurfacesAuditWithPathKind()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            IncludePaths: new[] { "docs/" });
        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();
        fetcher.FetchAsync("acme", "platform", 9, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "src/Only.cs" });

        var (connector, bus) = BuildConnector(config, fetcher);

        const string payload = """
        {
            "action": "opened",
            "installation": { "id": 1 },
            "repository": {
                "name": "platform",
                "full_name": "acme/platform",
                "owner": { "login": "acme" }
            },
            "pull_request": {
                "number": 9,
                "title": "Code-only PR",
                "body": "x",
                "head": { "ref": "feature" },
                "base": { "ref": "main" },
                "user": { "login": "alice" }
            }
        }
        """;

        var result = await connector.HandleWebhookAsync(
            "pull_request",
            payload,
            Sign(payload, Secret),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(WebhookOutcome.Ignored);
        await bus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ConnectorEventFiltered
                && e.Details!.Value.GetProperty("filter_kind").GetString() == "include_path"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookAsync_PullRequestWithoutIncludePaths_DoesNotCallFetcher()
    {
        // Lazy-fetch invariant: PR webhooks pay zero extra GitHub API cost
        // when IncludePaths is unset. Label-only bindings keep their
        // single-request behaviour.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            IncludeLabels: new[] { "spring-voyage" });
        var fetcher = Substitute.For<IGitHubPullRequestFilesFetcher>();

        var (connector, _) = BuildConnector(config, fetcher);

        const string payload = """
        {
            "action": "opened",
            "installation": { "id": 1 },
            "repository": {
                "name": "platform",
                "full_name": "acme/platform",
                "owner": { "login": "acme" }
            },
            "pull_request": {
                "number": 9,
                "title": "x",
                "head": { "ref": "feature" },
                "base": { "ref": "main" },
                "user": { "login": "alice" }
            }
        }
        """;

        await connector.HandleWebhookAsync(
            "pull_request",
            payload,
            Sign(payload, Secret),
            TestContext.Current.CancellationToken);

        await fetcher.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    private static (GitHubConnector connector, IActivityEventBus bus) BuildConnector(
        UnitGitHubConfig config,
        IGitHubPullRequestFilesFetcher? fetcher = null)
    {
        var options = new GitHubConnectorOptions
        {
            WebhookSecret = Secret,
            InstallationId = 1,
        };

        // Pin a known AppInstallationId on the binding so the webhook
        // resolver (#2456) can match the payload's installation.id
        // against this unit's binding. Tests inject the same id on the
        // payload's `installation` object so the resolver finds the
        // unit and the filter chain receives the unit-addressed message
        // it expects.
        var pinnedConfig = config with { AppInstallationId = 1 };

        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(pinnedConfig, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup.ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(TargetUnitHex, binding) });

        var bus = Substitute.For<IActivityEventBus>();

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            configStore: store,
            bindingLookup: lookup,
            activityEventBus: bus,
            filesFetcher: fetcher);

        var connector = new GitHubConnector(
            new GitHubAppAuth(options, NullLoggerFactory.Instance),
            handler,
            new WebhookSignatureValidator(),
            options,
            new GitHubRateLimitTracker(new GitHubRetryOptions(), NullLoggerFactory.Instance),
            new GitHubRetryOptions(),
            NullLoggerFactory.Instance);
        return (connector, bus);
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
