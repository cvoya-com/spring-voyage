// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using System.Security.Cryptography;
using System.Text;
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
/// Exercises the webhook-driven invalidation fan-out end-to-end: cache a PR
/// read, deliver a <c>pull_request.edited</c> payload to the connector, and
/// assert the next read misses (i.e., re-queries Octokit).
/// </summary>
public class WebhookCacheInvalidationTests
{
    private sealed class CountingConnector : GitHubConnector
    {
        private readonly IGitHubClient _client;

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
            => Task.FromResult(_client);
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WaitForCacheMissAsync(
        InMemoryGitHubResponseCache cache,
        CacheKey key,
        TimeSpan timeout)
    {
        // Webhook invalidation is fire-and-forget on the thread pool so the
        // handler returns instantly to GitHub. Poll briefly until the entry
        // is gone (or we give up and let the assertion fail).
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            var hit = await cache.TryGetAsync<JsonElement>(key, TestContext.Current.CancellationToken);
            if (hit is null)
            {
                return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task HandleWebhook_PullRequestEdited_InvalidatesCachedRead()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new GitHubConnectorOptions
        {
            InstallationId = 1,
            WebhookSecret = "s3cret",
        };
        var cacheOptions = new GitHubResponseCacheOptions
        {
            Enabled = true,
            DefaultTtl = TimeSpan.FromMinutes(5),
            CleanupInterval = TimeSpan.Zero,
        };
        using var cache = new InMemoryGitHubResponseCache(cacheOptions, NullLoggerFactory.Instance, time);
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Get("cvoya", "spring", 7).Returns(_ => PrTestHelpers.CreatePullRequest(
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

        var connector = new CountingConnector(client, options, cache);
        var invoker = new CachedSkillInvoker(cache, cacheOptions, NullLoggerFactory.Instance);
        var registry = new GitHubSkillRegistry(
            connector,
            new LabelStateMachine(LabelStateMachineOptions.Default()),
            Substitute.For<IGitHubInstallationsClient>(),
            NullLoggerFactory.Instance,
            invoker);

        var args = JsonSerializer.SerializeToElement(new { owner = "cvoya", repo = "spring", number = 7 });

        // Prime the cache.
        await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);
        await client.PullRequest.Received(1).Get("cvoya", "spring", 7);

        // Second call hits cache — no new Octokit call.
        await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);
        await client.PullRequest.Received(1).Get("cvoya", "spring", 7);

        // Simulate GitHub delivering a pull_request.edited webhook.
        var payload = """
        {
          "action": "edited",
          "repository": { "name": "spring", "owner": { "login": "cvoya" }, "full_name": "cvoya/spring" },
          "pull_request": {
            "number": 7, "title": "T", "state": "open",
            "head": { "ref": "h", "sha": "s" },
            "base": { "ref": "m", "sha": "b" },
            "user": { "login": "a" }
          }
        }
        """;
        var sig = Sign(payload, options.WebhookSecret);

        var result = connector.HandleWebhook("pull_request", payload, sig);
        result.Outcome.ShouldBe(WebhookOutcome.Translated);

        var prKey = new CacheKey("pull_request", "cvoya/spring#7",
            [CacheTags.Repository("cvoya", "spring"), CacheTags.PullRequest("cvoya", "spring", 7)]);
        await WaitForCacheMissAsync(cache, prKey, TimeSpan.FromSeconds(2));

        // Third call after webhook: cache is gone, must re-query Octokit.
        await registry.InvokeAsync("github_get_pull_request", args, TestContext.Current.CancellationToken);
        await client.PullRequest.Received(2).Get("cvoya", "spring", 7);
    }

    [Fact]
    public async Task HandleWebhook_InvalidSignature_DoesNotInvalidate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new GitHubConnectorOptions { InstallationId = 1, WebhookSecret = "s3cret" };
        var cacheOptions = new GitHubResponseCacheOptions { Enabled = true, DefaultTtl = TimeSpan.FromMinutes(5), CleanupInterval = TimeSpan.Zero };
        using var cache = new InMemoryGitHubResponseCache(cacheOptions, NullLoggerFactory.Instance, time);
        var client = Substitute.For<IGitHubClient>();
        var connector = new CountingConnector(client, options, cache);

        var key = new CacheKey(
            "pull_request",
            "cvoya/spring#7",
            [CacheTags.PullRequest("cvoya", "spring", 7)]);
        var payloadElement = JsonSerializer.SerializeToElement(new { ok = true });
        await cache.SetAsync(key, payloadElement, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Send a webhook with a deliberately wrong signature — HandleWebhook
        // must short-circuit on the signature check before touching the cache.
        var rawPayload = """{"action":"edited","repository":{"name":"spring","owner":{"login":"cvoya"}},"pull_request":{"number":7}}""";
        var result = connector.HandleWebhook("pull_request", rawPayload, signature: "sha256=wrong");
        result.Outcome.ShouldBe(WebhookOutcome.InvalidSignature);

        // Cache entry is intact — an unauthenticated webhook cannot be used
        // to flush reads out-of-band.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var hit = await cache.TryGetAsync<JsonElement>(key, TestContext.Current.CancellationToken);
        hit.ShouldNotBeNull();
    }
}