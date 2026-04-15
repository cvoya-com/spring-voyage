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

public class GetPrReviewBundleSkillTests
{
    private readonly IGitHubGraphQLClient _graphQLClient;
    private readonly GetPrReviewBundleSkill _skill;

    public GetPrReviewBundleSkillTests()
    {
        _graphQLClient = Substitute.For<IGitHubGraphQLClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new GetPrReviewBundleSkill(_graphQLClient, loggerFactory);
    }

    private static JsonElement FullBatchResponse() => JsonSerializer.Deserialize<JsonElement>("""
        {
          "reviews_pr": {
            "pullRequest": {
              "reviews": {
                "nodes": [
                  {
                    "databaseId": 111,
                    "state": "APPROVED",
                    "body": "LGTM",
                    "submittedAt": "2025-03-10T00:00:00Z",
                    "url": "https://github.com/o/r/pull/1#pullrequestreview-111",
                    "commit": { "oid": "abc" },
                    "author": { "login": "alice" }
                  }
                ]
              }
            }
          },
          "review_comments_pr": {
            "pullRequest": {
              "reviewThreads": {
                "nodes": [
                  {
                    "id": "T_1",
                    "isResolved": false,
                    "path": "src/x.cs",
                    "line": 42,
                    "comments": {
                      "nodes": [
                        {
                          "databaseId": 222,
                          "body": "nit: rename",
                          "path": "src/x.cs",
                          "position": 10,
                          "originalPosition": 10,
                          "diffHunk": "@@ -1 +1 @@",
                          "commit": { "oid": "abc" },
                          "url": "https://github.com/o/r/pull/1#discussion_r222",
                          "createdAt": "2025-03-10T00:00:00Z",
                          "updatedAt": "2025-03-10T00:00:00Z",
                          "author": { "login": "alice" }
                        }
                      ]
                    }
                  }
                ]
              }
            }
          },
          "review_threads_pr": {
            "pullRequest": {
              "reviewThreads": {
                "nodes": [
                  {
                    "id": "T_1",
                    "isResolved": false,
                    "isOutdated": false,
                    "path": "src/x.cs",
                    "line": 42,
                    "comments": { "nodes": [ { "id": "C_1", "databaseId": 222, "body": "nit: rename", "author": { "login": "alice" } } ] }
                  },
                  {
                    "id": "T_2",
                    "isResolved": true,
                    "isOutdated": false,
                    "path": "src/y.cs",
                    "line": 7,
                    "comments": { "nodes": [] }
                  }
                ]
              }
            }
          }
        }
        """);

    [Fact]
    public async Task ExecuteAsync_HappyPath_AggregatesAllThreeSections()
    {
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(FullBatchResponse());

        var result = await _skill.ExecuteAsync(
            "o", "r", 1,
            maxPerSection: 100,
            TestContext.Current.CancellationToken);

        // Exactly one GraphQL call covering all three sections.
        await _graphQLClient.Received(1)
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

        result.GetProperty("reviews").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("review_comments").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("review_threads").GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("review_threads").GetProperty("unresolved_count").GetInt32().ShouldBe(1);
        result.GetProperty("review_threads").GetProperty("has_unresolved_review_threads").GetBoolean().ShouldBeTrue();

        // Flattened review-comment preserves thread resolution context.
        var firstComment = result.GetProperty("review_comments").GetProperty("items")[0];
        firstComment.GetProperty("thread_id").GetString().ShouldBe("T_1");
        firstComment.GetProperty("is_resolved").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_SurfacesErrorsWithoutPoisoningOtherSections()
    {
        // Only the reviews alias is present; comments and threads are missing.
        var partial = JsonSerializer.Deserialize<JsonElement>("""
            {
              "reviews_pr": {
                "pullRequest": {
                  "reviews": { "nodes": [ { "databaseId": 1, "state": "COMMENTED", "body": "", "author": { "login": "a" } } ] }
                }
              }
            }
            """);

        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(partial);

        var result = await _skill.ExecuteAsync(
            "o", "r", 1,
            maxPerSection: 100,
            TestContext.Current.CancellationToken);

        result.GetProperty("reviews").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("review_comments").GetProperty("count").GetInt32().ShouldBe(0);
        result.GetProperty("review_threads").GetProperty("count").GetInt32().ShouldBe(0);

        var errors = result.GetProperty("errors");
        errors.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_SingleBatchedCall_NotThreeSeparateRequests()
    {
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(FullBatchResponse());

        string? capturedQuery = null;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(FullBatchResponse());

        await _skill.ExecuteAsync(
            "o", "r", 1,
            maxPerSection: 50,
            TestContext.Current.CancellationToken);

        // A single GraphQL call means the rate-limit tracker observes
        // one `graphql` decrement instead of three separate decrements
        // (one `graphql` + two `core` in the pre-migration world).
        await _graphQLClient.Received(1)
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain(PrReviewBundleBatch.ReviewsAlias + ":");
        capturedQuery.ShouldContain(PrReviewBundleBatch.ReviewCommentsAlias + ":");
        capturedQuery.ShouldContain(PrReviewBundleBatch.ReviewThreadsAlias + ":");
        capturedQuery.ShouldContain("query Batch {");
    }

    [Fact]
    public async Task ExecuteAsync_ClampsMaxPerSection()
    {
        string? capturedQuery = null;
        _graphQLClient
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(FullBatchResponse());

        await _skill.ExecuteAsync(
            "o", "r", 1,
            maxPerSection: 10_000,
            TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("first: 100");
        capturedQuery.ShouldNotContain("first: 10000");
    }
}