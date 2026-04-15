// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class GetPriorWorkContextSkillTests
{
    private readonly IGitHubGraphQLClient _graphQLClient;
    private readonly GetPriorWorkContextSkill _skill;

    public GetPriorWorkContextSkillTests()
    {
        _graphQLClient = Substitute.For<IGitHubGraphQLClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new GetPriorWorkContextSkill(_graphQLClient, loggerFactory);
    }

    private static JsonElement MockBucketedResponse()
    {
        // Each bucket returns one item with a distinct number so we can
        // confirm routing via the projected output.
        static string Node(int n, string typename) => $$"""
            {
              "__typename": "{{typename}}",
              "number": {{n}},
              "title": "item-{{n}}",
              "url": "https://github.com/owner/repo/{{(typename == "PullRequest" ? "pull" : "issues")}}/{{n}}",
              "state": "OPEN",
              "createdAt": "2025-01-01T00:00:00Z",
              "updatedAt": "2025-01-02T00:00:00Z",
              "author": { "login": "bot-user" }
            }
            """;

        var json = $$"""
            {
              "mentions_search":  { "nodes": [ {{Node(1, "Issue")}} ] },
              "authored_search":  { "nodes": [ {{Node(2, "PullRequest")}} ] },
              "commented_search": { "nodes": [ {{Node(3, "Issue")}} ] },
              "assigned_search":  { "nodes": [ {{Node(4, "Issue")}} ] }
            }
            """;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBucketedSummaryShape()
    {
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(MockBucketedResponse());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        // Exactly one GraphQL call (not four REST calls).
        await _graphQLClient.Received(1)
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

        result.GetProperty("user").GetString().ShouldBe("bot-user");
        result.GetProperty("repository").GetProperty("full_name").GetString().ShouldBe("owner/repo");
        result.GetProperty("mentions").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("authored_pull_requests").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("commented_issues").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("assigned_issues").GetProperty("count").GetInt32().ShouldBe(1);

        // The authored bucket item came in as a PullRequest — projected type should reflect that.
        result.GetProperty("authored_pull_requests")
            .GetProperty("items")[0]
            .GetProperty("type")
            .GetString()
            .ShouldBe("pull_request");

        // Other buckets' items should be classified as issues.
        result.GetProperty("mentions")
            .GetProperty("items")[0]
            .GetProperty("type")
            .GetString()
            .ShouldBe("issue");
    }

    [Fact]
    public async Task ExecuteAsync_IssuesDistinctQualifiersPerBucket()
    {
        string? capturedQuery = null;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(MockBucketedResponse());

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("mentions:bot-user");
        capturedQuery.ShouldContain("author:bot-user is:pr");
        capturedQuery.ShouldContain("commenter:bot-user is:issue");
        capturedQuery.ShouldContain("assignee:bot-user is:issue");
        // All four buckets in a single batch — one query Batch { ... } envelope.
        capturedQuery.ShouldContain("query Batch {");
        capturedQuery.ShouldContain("mentions_search:");
        capturedQuery.ShouldContain("authored_search:");
        capturedQuery.ShouldContain("commented_search:");
        capturedQuery.ShouldContain("assigned_search:");
    }

    [Fact]
    public async Task ExecuteAsync_ClampsMaxPerBucket()
    {
        string? capturedQuery = null;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(MockBucketedResponse());

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 10_000,
            TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        // Each bucket's inline `first:` argument should be clamped to 100.
        capturedQuery!.ShouldContain("first: 100");
        capturedQuery.ShouldNotContain("first: 10000");
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_SurfacesPerBucketError()
    {
        // One bucket missing from the response — partial failure pattern
        // inherited from GraphQLBatch semantics. The skill must NOT throw;
        // the missing bucket surfaces as an error string, other buckets
        // return normally.
        var json = """
            {
              "mentions_search":  { "nodes": [] },
              "authored_search":  { "nodes": [] },
              "commented_search": { "nodes": [] }
            }
            """;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<JsonElement>(json));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        result.GetProperty("assigned_issues").GetProperty("count").GetInt32().ShouldBe(0);
        result.GetProperty("assigned_issues").TryGetProperty("error", out var err).ShouldBeTrue();
        err.GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_SincePredicate_EmbeddedInQuery()
    {
        string? capturedQuery = null;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(MockBucketedResponse());

        var since = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("updated:>2025-03-01T00:00:00Z");
    }
}