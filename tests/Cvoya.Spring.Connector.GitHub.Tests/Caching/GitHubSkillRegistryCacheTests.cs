// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end: invoke a cached read skill through
/// <see cref="GitHubSkillRegistry"/> twice and assert the second call
/// returns from cache (no second Octokit call, and therefore no rate-limit
/// header processing). Pairs with
/// <see cref="WebhookCacheInvalidationTests"/> for the invalidation path.
/// </summary>
public class GitHubSkillRegistryCacheTests
{
    private sealed class CountingConnector : GitHubConnector
    {
        private readonly IGitHubClient _client;
        public int AuthCalls { get; private set; }

        public CountingConnector(
            IGitHubClient client,
            GitHubConnectorOptions options,
            IGitHubResponseCache responseCache)
            : base(
                new GitHubAppAuth(options, NullLoggerFactory.Instance),
                new GitHubWebhookHandler(options, NullLoggerFactory.Instance),
                new WebhookSignatureValidator(),
                options,
                new GitHubRateLimitTracker(new GitHubRetryOptions(), NullLoggerFactory.Instance),
                new GitHubRetryOptions(),
                NullLoggerFactory.Instance,
                responseCache: responseCache)
        {
            _client = client;
        }

        public override Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default)
        {
            AuthCalls++;
            return Task.FromResult(_client);
        }
    }

    private static (GitHubSkillRegistry registry, CountingConnector connector, IGitHubClient client, InMemoryGitHubResponseCache cache)
        Build(bool cacheEnabled)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cacheOptions = new GitHubResponseCacheOptions
        {
            Enabled = cacheEnabled,
            DefaultTtl = TimeSpan.FromMinutes(1),
            CleanupInterval = TimeSpan.Zero,
        };
        IGitHubResponseCache cache = cacheEnabled
            ? new InMemoryGitHubResponseCache(cacheOptions, NullLoggerFactory.Instance, time)
            : NoOpGitHubResponseCache.Instance;

        var client = Substitute.For<IGitHubClient>();
        var options = new GitHubConnectorOptions { InstallationId = 1 };
        var connector = new CountingConnector(client, options, cache);
        var invoker = new CachedSkillInvoker(cache, cacheOptions, NullLoggerFactory.Instance);
        var registry = new GitHubSkillRegistry(
            connector,
            new LabelStateMachine(LabelStateMachineOptions.Default()),
            Substitute.For<IGitHubInstallationsClient>(),
            NullLoggerFactory.Instance,
            invoker);

        // Wire up a canned PR response so every Octokit call returns the same
        // shape. Using NSubstitute lets us count calls via ReceivedCalls().
        client.PullRequest.Get("owner", "repo", 7).Returns(_ => PrTestHelpers.CreatePullRequest(
            number: 7,
            title: "T",
            body: "B",
            htmlUrl: "https://example",
            authorLogin: "a",
            headRef: "h",
            headSha: "s",
            baseRef: "m",
            labels: [],
            assigneeLogins: [],
            draft: false));

        return (registry, connector, client, (cache as InMemoryGitHubResponseCache)!);
    }

    [Fact]
    public async Task InvokeAsync_GetPullRequest_SecondCallHitsCache()
    {
        var (registry, connector, client, _) = Build(cacheEnabled: true);

        var args = JsonSerializer.SerializeToElement(new { owner = "owner", repo = "repo", number = 7 });

        var r1 = await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);
        var r2 = await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);

        r1.GetProperty("number").GetInt32().ShouldBe(7);
        r2.GetProperty("number").GetInt32().ShouldBe(7);

        // Second call must be served entirely from cache:
        //   * the connector's CreateAuthenticatedClientAsync is still called
        //     (we authenticate BEFORE dispatching because we don't know
        //     up-front whether the skill will cache); but
        //   * the Octokit API call itself is skipped, which is what matters
        //     for the rate-limit header processing.
        await client.PullRequest.Received(1).Get("owner", "repo", 7);
        connector.AuthCalls.ShouldBe(2);
    }

    [Fact]
    public async Task InvokeAsync_OptOut_EveryCallGoesToOctokit()
    {
        var (registry, _, client, _) = Build(cacheEnabled: false);

        var args = JsonSerializer.SerializeToElement(new { owner = "owner", repo = "repo", number = 7 });

        await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);

        await client.PullRequest.Received(2).Get("owner", "repo", 7);
    }

    [Fact]
    public async Task InvokeAsync_DifferentQueryParams_DoNotShareEntry()
    {
        var (registry, _, client, _) = Build(cacheEnabled: true);

        client.PullRequest.GetAllForRepository("o", "r", Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns(_ => (IReadOnlyList<PullRequest>)new List<PullRequest>());

        var args1 = JsonSerializer.SerializeToElement(new { owner = "o", repo = "r", state = "open", maxResults = 10 });
        var args2 = JsonSerializer.SerializeToElement(new { owner = "o", repo = "r", state = "closed", maxResults = 10 });

        await registry.InvokeAsync("github_list_pull_requests", args1, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github_list_pull_requests", args2, TestContext.Current.CancellationToken);

        // state=open vs state=closed are different discriminators; both must
        // hit Octokit at least once.
        await client.PullRequest.Received(2).GetAllForRepository(
            "o", "r", Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>());
    }
}