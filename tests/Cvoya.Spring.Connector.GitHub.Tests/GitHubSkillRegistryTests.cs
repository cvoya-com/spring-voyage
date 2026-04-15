// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class GitHubSkillRegistryTests
{
    private readonly GitHubSkillRegistry _registry;

    public GitHubSkillRegistryTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var options = new GitHubConnectorOptions();
        var auth = new GitHubAppAuth(options, loggerFactory);
        var webhookHandler = new GitHubWebhookHandler(options, loggerFactory);
        var signatureValidator = new WebhookSignatureValidator();
        var retryOptions = new GitHubRetryOptions();
        var tracker = new GitHubRateLimitTracker(retryOptions, loggerFactory);
        var connector = new GitHubConnector(auth, webhookHandler, signatureValidator, options, tracker, retryOptions, loggerFactory);
        var labelStateMachine = new Cvoya.Spring.Connector.GitHub.Labels.LabelStateMachine(
            Cvoya.Spring.Connector.GitHub.Labels.LabelStateMachineOptions.Default());
        var installations = Substitute.For<IGitHubInstallationsClient>();
        _registry = new GitHubSkillRegistry(connector, labelStateMachine, installations, loggerFactory);
    }

    [Fact]
    public void GetToolDefinitions_ReturnsAllTools()
    {
        var tools = _registry.GetToolDefinitions();

        tools.Count().ShouldBe(49);
        tools.Select(t => t.Name).ShouldBe(new[]
        {
            "github_create_branch",
            "github_create_pull_request",
            "github_comment_on_issue",
            "github_comment_on_pull_request",
            "github_read_file",
            "github_write_file",
            "github_delete_file",
            "github_list_files",
            "github_get_issue_details",
            "github_get_pull_request_diff",
            "github_manage_labels",
            "github_create_issue",
            "github_close_issue",
            "github_list_issues",
            "github_assign_issue",
            "github_get_issue_author",
            "github_update_comment",
            "github_list_comments",
            "github_get_pull_request",
            "github_list_pull_requests",
            "github_find_pull_request_for_branch",
            "github_list_pull_requests_by_author",
            "github_list_pull_requests_by_assignee",
            "github_list_pull_request_reviews",
            "github_list_pull_request_review_comments",
            "github_has_approved_review",
            "github_merge_pull_request",
            "github_enable_auto_merge",
            "github_update_branch",
            "github_request_pull_request_review",
            "github_ensure_issue_linked_to_pull_request",
            "github_search_mentions",
            "github_get_prior_work_context",
            "github_label_transition",
            "github_list_review_threads",
            "github_resolve_review_thread",
            "github_unresolve_review_thread",
            "github_get_pr_review_bundle",
            "github_list_webhooks",
            "github_update_webhook",
            "github_delete_webhook",
            "github_test_webhook",
            "github_list_installations",
            "github_list_installation_repositories",
            "github_find_installation_for_repo",
            "github_list_projects_v2",
            "github_get_project_v2",
            "github_list_project_v2_items",
            "github_get_project_v2_item",
        }, ignoreOrder: true);
    }

    [Fact]
    public void GetToolDefinitions_AllHaveValidJsonSchemas()
    {
        var tools = _registry.GetToolDefinitions();

        foreach (var tool in tools)
        {
            tool.Name.ShouldNotBeNullOrWhiteSpace();
            tool.Description.ShouldNotBeNullOrWhiteSpace();
            tool.InputSchema.ValueKind.ShouldBe(JsonValueKind.Object);
            tool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
            tool.InputSchema.TryGetProperty("properties", out _).ShouldBeTrue();
        }
    }
}