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

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2Edited_EmitsProjectAndListTags()
    {
        // projects_v2 webhooks live at org scope — "organization.login" and a
        // top-level "projects_v2" object carry the owner/number the cache
        // keys the tag under. Both the per-project tag and the owner-wide
        // list tag must be emitted so listing and single-board reads refresh
        // in lockstep when a project is renamed / closed / deleted.
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2": { "id": 42, "node_id": "PVT_1", "number": 7, "title": "Delivery", "closed": false }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2", payload);

        tags.ShouldBe(["project-v2:acme/7", "projects-v2-list:acme"]);
    }

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2ItemEdited_EmitsItemTag()
    {
        // Item events carry the item's GraphQL node id as projects_v2_item.node_id.
        // The per-item tag is what the github.get_project_v2_item skill caches
        // under, so emitting that tag is necessary AND sufficient for item reads.
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2_item": {
            "id": 100, "node_id": "PVTI_1", "project_node_id": "PVT_1", "content_type": "Issue"
          }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2_item", payload);

        tags.ShouldBe(["project-v2-item:PVTI_1"]);
    }

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2ItemArchived_EmitsItemTag()
    {
        // Same derivation path as "edited" — the action only changes the
        // translated message's intent, not the cache-tag set.
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "archived",
          "organization": { "login": "acme" },
          "projects_v2_item": {
            "id": 100, "node_id": "PVTI_ARCHIVED", "project_node_id": "PVT_1",
            "content_type": "Issue", "archived_at": "2026-04-13T12:00:00Z"
          }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2_item", payload);

        tags.ShouldBe(["project-v2-item:PVTI_ARCHIVED"]);
    }

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2_MissingNumber_FallsBackToListTag()
    {
        // Degraded projects_v2 payload (no "number" field). Still safe to
        // flush the owner-wide list so a new / renamed board surfaces; the
        // per-project tag is simply omitted.
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "created",
          "organization": { "login": "acme" },
          "projects_v2": { "title": "Untitled" }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2", payload);

        tags.ShouldBe(["projects-v2-list:acme"]);
    }

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2_MissingOrganization_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "projects_v2": { "number": 7 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2", payload);

        tags.ShouldBeEmpty();
    }

    [Fact]
    public void DeriveInvalidationTags_ProjectsV2Item_MissingNodeId_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var payload = Parse("""
        {
          "action": "edited",
          "organization": { "login": "acme" },
          "projects_v2_item": { "id": 100 }
        }
        """);

        var tags = handler.DeriveInvalidationTags("projects_v2_item", payload);

        tags.ShouldBeEmpty();
    }
}
