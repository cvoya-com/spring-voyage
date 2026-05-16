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

        tools.Count().ShouldBe(54);
        tools.Select(t => t.Name).ShouldBe(new[]
        {
            "github.create_branch",
            "github.create_pull_request",
            "github.comment_on_issue",
            "github.comment_on_pull_request",
            "github.read_file",
            "github.write_file",
            "github.delete_file",
            "github.list_files",
            "github.get_issue_details",
            "github.get_pull_request_diff",
            "github.manage_labels",
            "github.create_issue",
            "github.close_issue",
            "github.list_issues",
            "github.assign_issue",
            "github.get_issue_author",
            "github.update_comment",
            "github.list_comments",
            "github.get_pull_request",
            "github.list_pull_requests",
            "github.find_pull_request_for_branch",
            "github.list_pull_requests_by_author",
            "github.list_pull_requests_by_assignee",
            "github.list_pull_request_reviews",
            "github.list_pull_request_review_comments",
            "github.has_approved_review",
            "github.merge_pull_request",
            "github.enable_auto_merge",
            "github.update_branch",
            "github.request_pull_request_review",
            "github.ensure_issue_linked_to_pull_request",
            "github.search_mentions",
            "github.get_prior_work_context",
            "github.label_transition",
            "github.list_review_threads",
            "github.resolve_review_thread",
            "github.unresolve_review_thread",
            "github.get_pr_review_bundle",
            "github.list_webhooks",
            "github.update_webhook",
            "github.delete_webhook",
            "github.test_webhook",
            "github.list_installations",
            "github.list_installation_repositories",
            "github.find_installation_for_repo",
            "github.list_projects_v2",
            "github.get_project_v2",
            "github.list_project_v2_items",
            "github.get_project_v2_item",
            "github.add_project_v2_item",
            "github.update_project_v2_item_field_value",
            "github.archive_project_v2_item",
            "github.delete_project_v2_item",
            "github.get_authenticated_user",
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
