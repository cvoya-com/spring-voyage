// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Checks the <see cref="GitHubWebhookHandler.DeriveInvalidationTags"/>
/// contract for each event type the handler translates. The derivation is
/// the wire between GitHub's push notifications and the response-cache
/// invalidation fan-out.
/// </summary>
public class WebhookInvalidationTagTests
{
    private static GitHubWebhookHandler CreateHandler() =>
        new(new GitHubConnectorOptions { DefaultTargetUnitPath = "unit-1" },
            NullLoggerFactory.Instance);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void DeriveInvalidationTags_IssuesEdited_EmitsIssueTag()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "repository": { "name": "spring", "owner": { "login": "cvoya" } },
          "issue": { "number": 42 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("issues", payload);

        tags.ShouldBe(["issue:cvoya/spring#42"]);
    }

    [Fact]
    public void DeriveInvalidationTags_IssueCommentCreated_EmitsIssueAndPullRequestTags()
    {
        var handler = CreateHandler();
        // GitHub sends the same event for PR comments and issue comments, so
        // both tags must be emitted — a PR conversation comment cache keyed
        // under pr:X should flush, as should an issue comment cache keyed
        // under issue:X.
        var payload = Parse("""
        {
          "action": "created",
          "repository": { "name": "spring", "owner": { "login": "cvoya" } },
          "issue": { "number": 10 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("issue_comment", payload);

        tags.ShouldBe(["issue:cvoya/spring#10", "pr:cvoya/spring#10"]);
    }

    [Fact]
    public void DeriveInvalidationTags_PullRequestEdited_EmitsPrAndIssueTag()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "repository": { "name": "spring", "owner": { "login": "cvoya" } },
          "pull_request": { "number": 7 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("pull_request", payload);

        tags.ShouldBe(["pr:cvoya/spring#7", "issue:cvoya/spring#7"]);
    }

    [Fact]
    public void DeriveInvalidationTags_PullRequestReviewSubmitted_EmitsPrTag()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "submitted",
          "repository": { "name": "spring", "owner": { "login": "cvoya" } },
          "pull_request": { "number": 7 },
          "review": { "id": 1 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("pull_request_review", payload);

        tags.ShouldContain("pr:cvoya/spring#7");
    }

    [Fact]
    public void DeriveInvalidationTags_UnhandledEvent_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "pushed",
          "repository": { "name": "spring", "owner": { "login": "cvoya" } }
        }
        """);

        var tags = handler.DeriveInvalidationTags("push", payload);

        tags.ShouldBeEmpty();
    }

    [Fact]
    public void DeriveInvalidationTags_MissingRepository_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        { "action": "created" }
        """);

        var tags = handler.DeriveInvalidationTags("installation", payload);

        tags.ShouldBeEmpty();
    }
}