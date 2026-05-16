// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using System.Net;
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
using Octokit.Internal;

using Shouldly;

using Xunit;

/// <summary>
/// D13 wire-up: Projects v2 read skills flow through
/// <see cref="CachedSkillInvoker"/> with the per-resource tag scheme and the
/// short projects-v2-specific TTL default. Also covers the webhook
/// invalidation fan-out for <c>projects_v2</c> / <c>projects_v2_item</c>.
/// </summary>
public class ProjectsV2CacheTests
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

    // Minimal GraphQL envelope used by the Projects v2 read queries. Each
    // skill only reads the outer "repositoryOwner.projectV2" / "node" shape
    // to compose its JsonElement result; we return the absolute minimum so
    // the skill can successfully deserialize without exercising any real
    // Projects v2 board behaviour (that is covered by the skill-level tests).
    private const string ProjectByNumberResponse = """
    {
      "data": {
        "repositoryOwner": {
          "projectV2": {
            "id": "PVT_1",
            "number": 7,
            "title": "Delivery",
            "url": "https://example/project/7",
            "closed": false,
            "public": true,
            "shortDescription": null,
            "readme": null,
            "createdAt": "2026-04-13T12:00:00Z",
            "updatedAt": "2026-04-13T12:00:00Z",
            "fields": { "nodes": [] }
          }
        }
      }
    }
    """;

    private const string ProjectListResponse = """
    {
      "data": {
        "repositoryOwner": {
          "projectsV2": {
            "nodes": []
          }
        }
      }
    }
    """;

    private const string ProjectItemsResponse = """
    {
      "data": {
        "repositoryOwner": {
          "projectV2": {
            "id": "PVT_1",
            "title": "Delivery",
            "items": {
              "pageInfo": { "hasNextPage": false, "endCursor": null },
              "nodes": []
            }
          }
        }
      }
    }
    """;

    private const string ProjectItemByIdResponse = """
    {
      "data": {
        "node": {
          "id": "PVTI_1",
          "type": "ISSUE",
          "isArchived": false,
          "createdAt": "2026-04-13T12:00:00Z",
          "updatedAt": "2026-04-13T12:00:00Z",
          "content": null,
          "fieldValues": { "nodes": [] }
        }
      }
    }
    """;

    // Builds the IApiResponse<JsonElement> envelope Octokit's IConnection.Post
    // overload returns. Mirrors OctokitGraphQLClientTests.Envelope.
    private static ApiResponse<JsonElement> Envelope(string jsonBody)
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.OK);
        return new ApiResponse<JsonElement>(response, JsonSerializer.Deserialize<JsonElement>(jsonBody));
    }

    private static (GitHubSkillRegistry registry, CountingConnector connector, IGitHubClient client,
        IConnection connection, InMemoryGitHubResponseCache cache, int[] postCounter)
        Build(GitHubResponseCacheOptions? cacheOptions = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        cacheOptions ??= new GitHubResponseCacheOptions
        {
            Enabled = true,
            DefaultTtl = TimeSpan.FromMinutes(5),
            CleanupInterval = TimeSpan.Zero,
        };
        var cache = new InMemoryGitHubResponseCache(cacheOptions, NullLoggerFactory.Instance, time);

        var client = Substitute.For<IGitHubClient>();
        var connection = Substitute.For<IConnection>();
        client.Connection.Returns(connection);

        var options = new GitHubConnectorOptions { InstallationId = 1, WebhookSecret = "s3cret" };
        var connector = new CountingConnector(client, options, cache);
        var invoker = new CachedSkillInvoker(cache, cacheOptions, NullLoggerFactory.Instance);
        var registry = new GitHubSkillRegistry(
            connector,
            new LabelStateMachine(LabelStateMachineOptions.Default()),
            Substitute.For<IGitHubInstallationsClient>(),
            NullLoggerFactory.Instance,
            invoker);

        // Mutable counter box so tests can observe the number of GraphQL
        // POSTs without threading Received() calls through every assertion.
        var postCounter = new[] { 0 };

        // Route every GraphQL POST to the right canned response based on the
        // query contents. Projects v2 has four distinct shapes, so we peek at
        // the query string to decide which envelope to return. This keeps the
        // test end-to-end (skill → invoker → graphql client → connection)
        // without pulling in a real GraphQL transport.
        connection
            .Post<JsonElement>(
                Arg.Any<Uri>(),
                Arg.Any<object>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                postCounter[0]++;
                var body = (IDictionary<string, object?>)call.Args()[1]!;
                var query = (string)body["query"]!;
                if (query.Contains("projectsV2(first", StringComparison.Ordinal))
                {
                    return Envelope(ProjectListResponse);
                }
                if (query.Contains("node(id:", StringComparison.Ordinal))
                {
                    return Envelope(ProjectItemByIdResponse);
                }
                if (query.Contains("items(", StringComparison.Ordinal))
                {
                    return Envelope(ProjectItemsResponse);
                }
                return Envelope(ProjectByNumberResponse);
            });

        return (registry, connector, client, connection, cache, postCounter);
    }

    [Fact]
    public async Task InvokeAsync_GetProjectV2_SecondCallHitsCache()
    {
        var (registry, _, _, _, _, posts) = Build();
        var args = JsonSerializer.SerializeToElement(new { owner = "acme", number = 7 });

        var first = await registry.InvokeAsync("github.get_project_v2", args, TestContext.Current.CancellationToken);
        var second = await registry.InvokeAsync("github.get_project_v2", args, TestContext.Current.CancellationToken);

        first.GetProperty("found").GetBoolean().ShouldBeTrue();
        second.GetProperty("found").GetBoolean().ShouldBeTrue();

        // Exactly one underlying GraphQL POST — the second read came back
        // from the cache. This is the same observable test shape D9 uses for
        // the PR-level cache (GitHubSkillRegistryCacheTests).
        posts[0].ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_GetProjectV2Item_SecondCallHitsCache()
    {
        var (registry, _, _, _, _, posts) = Build();
        var args = JsonSerializer.SerializeToElement(new { itemId = "PVTI_1" });

        await registry.InvokeAsync("github.get_project_v2_item", args, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.get_project_v2_item", args, TestContext.Current.CancellationToken);

        posts[0].ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_ListProjectV2Items_PageParamsCacheSeparately()
    {
        var (registry, _, _, _, _, posts) = Build();
        var argsLimit50 = JsonSerializer.SerializeToElement(new { owner = "acme", number = 7, limit = 50 });
        var argsLimit10 = JsonSerializer.SerializeToElement(new { owner = "acme", number = 7, limit = 10 });

        await registry.InvokeAsync("github.list_project_v2_items", argsLimit50, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.list_project_v2_items", argsLimit50, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.list_project_v2_items", argsLimit10, TestContext.Current.CancellationToken);

        // The two limits are different discriminators → one POST each; the
        // second limit=50 call hits the cache.
        posts[0].ShouldBe(2);
    }

    [Fact]
    public async Task HandleWebhook_ProjectsV2ItemEdited_InvalidatesItemCache()
    {
        var (registry, connector, _, _, cache, posts) = Build();
        var args = JsonSerializer.SerializeToElement(new { itemId = "PVTI_1" });

        // Prime the cache.
        await registry.InvokeAsync("github.get_project_v2_item", args, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(1);

        // Second call hits cache — no new POST.
        await registry.InvokeAsync("github.get_project_v2_item", args, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(1);

        // Deliver a projects_v2_item.edited event whose node_id matches.
        var payload = """
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2_item": {
            "id": 100,
            "node_id": "PVTI_1",
            "project_node_id": "PVT_1",
            "content_type": "Issue"
          },
          "changes": {
            "field_value": {
              "field_node_id": "F_STATUS",
              "field_type": "single_select",
              "from": { "id": "OPT_T", "name": "Todo" },
              "to":   { "id": "OPT_D", "name": "Done" }
            }
          }
        }
        """;
        var sig = Sign(payload, "s3cret");
        var result = connector.HandleWebhook("projects_v2_item", payload, sig);
        result.Outcome.ShouldBe(WebhookOutcome.Translated);

        var key = new CacheKey("project_v2_item", "PVTI_1", [CacheTags.ProjectV2Item("PVTI_1")]);
        await WaitForCacheMissAsync(cache, key, TimeSpan.FromSeconds(2));

        // Next read must re-query.
        await registry.InvokeAsync("github.get_project_v2_item", args, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(2);
    }

    [Fact]
    public async Task HandleWebhook_ProjectsV2ItemEdited_DoesNotInvalidateListCache()
    {
        // Tag disjointness guarantee: an item-level event must not flush the
        // list cache keyed on project-v2:<owner>/<number>. The two tags are
        // intentionally separate so an item drag doesn't nuke the entire
        // list just to re-render a single cell.
        var (registry, connector, _, _, cache, posts) = Build();
        var listArgs = JsonSerializer.SerializeToElement(new { owner = "acme", number = 7, limit = 50 });

        await registry.InvokeAsync("github.list_project_v2_items", listArgs, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(1);

        var payload = """
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2_item": {
            "id": 100,
            "node_id": "PVTI_1",
            "project_node_id": "PVT_1",
            "content_type": "Issue"
          }
        }
        """;
        var sig = Sign(payload, "s3cret");
        connector.HandleWebhook("projects_v2_item", payload, sig);

        // The item event should have no effect on the list cache. Poll
        // briefly just in case the background invalidator fires spuriously.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await registry.InvokeAsync("github.list_project_v2_items", listArgs, TestContext.Current.CancellationToken);
        // Still served from cache — no second POST.
        posts[0].ShouldBe(1);
    }

    [Fact]
    public async Task HandleWebhook_ProjectsV2Edited_InvalidatesProjectAndListTags()
    {
        var (registry, connector, _, _, cache, posts) = Build();
        var projectArgs = JsonSerializer.SerializeToElement(new { owner = "acme", number = 7 });
        var listArgs = JsonSerializer.SerializeToElement(new { owner = "acme", first = 30 });

        await registry.InvokeAsync("github.get_project_v2", projectArgs, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.list_projects_v2", listArgs, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(2);

        // Cached: both reads should skip the POST on repeat.
        await registry.InvokeAsync("github.get_project_v2", projectArgs, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.list_projects_v2", listArgs, TestContext.Current.CancellationToken);
        posts[0].ShouldBe(2);

        var payload = """
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2": { "id": 42, "node_id": "PVT_1", "number": 7, "title": "Delivery", "closed": false }
        }
        """;
        var sig = Sign(payload, "s3cret");
        connector.HandleWebhook("projects_v2", payload, sig);

        var projectKey = new CacheKey("project_v2", "acme/#7",
            [CacheTags.ProjectV2("acme", 7), CacheTags.ProjectV2List("acme")]);
        var listKey = new CacheKey("project_v2_list", "acme?first=30",
            [CacheTags.ProjectV2List("acme")]);
        await WaitForCacheMissAsync(cache, projectKey, TimeSpan.FromSeconds(2));
        await WaitForCacheMissAsync(cache, listKey, TimeSpan.FromSeconds(2));

        await registry.InvokeAsync("github.get_project_v2", projectArgs, TestContext.Current.CancellationToken);
        await registry.InvokeAsync("github.list_projects_v2", listArgs, TestContext.Current.CancellationToken);
        // Both reads missed — two new POSTs.
        posts[0].ShouldBe(4);
    }

    [Fact]
    public async Task GetProjectV2_UsesProjectsV2DefaultTtlWhenUnset()
    {
        // The options type pre-seeds project_v2 / project_v2_item /
        // project_v2_list to 30s so deployments that don't touch config still
        // get the short triage TTL. We verify both the seed and that the
        // global DefaultTtl does NOT apply to projects v2 resources.
        var options = new GitHubResponseCacheOptions
        {
            Enabled = true,
            DefaultTtl = TimeSpan.FromMinutes(10),
            CleanupInterval = TimeSpan.Zero,
        };

        options.ResolveTtl(GitHubResponseCacheOptions.Resources.ProjectV2)
            .ShouldBe(GitHubResponseCacheOptions.DefaultProjectV2Ttl);
        options.ResolveTtl(GitHubResponseCacheOptions.Resources.ProjectV2Item)
            .ShouldBe(GitHubResponseCacheOptions.DefaultProjectV2Ttl);
        options.ResolveTtl(GitHubResponseCacheOptions.Resources.ProjectV2List)
            .ShouldBe(GitHubResponseCacheOptions.DefaultProjectV2Ttl);

        // Unrelated resource still rides the global default.
        options.ResolveTtl(GitHubResponseCacheOptions.Resources.PullRequest)
            .ShouldBe(TimeSpan.FromMinutes(10));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetProjectV2_PerResourceConfigOverrideBeatsSeed()
    {
        // Config-style override of the projects v2 TTL replaces the seed.
        // This is the path the issue calls out: GitHub:ResponseCache:Ttls:project_v2.
        var options = new GitHubResponseCacheOptions
        {
            Enabled = true,
            CleanupInterval = TimeSpan.Zero,
        };
        options.Ttls[GitHubResponseCacheOptions.Resources.ProjectV2] = TimeSpan.FromSeconds(5);

        options.ResolveTtl(GitHubResponseCacheOptions.Resources.ProjectV2)
            .ShouldBe(TimeSpan.FromSeconds(5));
        // The sibling resources still ride the seeded default — overrides
        // are keyed by resource so one knob doesn't clobber the others.
        options.ResolveTtl(GitHubResponseCacheOptions.Resources.ProjectV2Item)
            .ShouldBe(GitHubResponseCacheOptions.DefaultProjectV2Ttl);

        await Task.CompletedTask;
    }

    private static async Task WaitForCacheMissAsync(
        InMemoryGitHubResponseCache cache,
        CacheKey key,
        TimeSpan timeout)
    {
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
}
