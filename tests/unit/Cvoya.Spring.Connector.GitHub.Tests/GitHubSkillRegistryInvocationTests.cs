// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies that <see cref="GitHubSkillRegistry"/> correctly implements
/// <see cref="ISkillRegistry"/> contract semantics independently of the
/// Octokit dispatch path (which is covered by the per-skill tests).
/// </summary>
public class GitHubSkillRegistryInvocationTests
{
    private readonly GitHubSkillRegistry _registry;

    public GitHubSkillRegistryInvocationTests()
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
    public void Name_IsGithub()
    {
        _registry.Name.ShouldBe("github");
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_ThrowsSkillNotFoundException()
    {
        var act = () => _registry.InvokeAsync(
            "github.not_a_tool",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        var ex = await Should.ThrowAsync<SkillNotFoundException>(act);
        ex.ToolName.ShouldBe("github.not_a_tool");
    }

    [Fact]
    public void GetToolDefinitions_CoversEveryDispatcher()
    {
        var tools = _registry.GetToolDefinitions().Select(t => t.Name).ToHashSet();

        tools.ShouldBe(new[]
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
            // The OAuth factory is nullable, so when the registry is built
            // without one (the App-only constructor overload used here) the
            // OAuth tools still appear in GetToolDefinitions — that's the
            // discovery surface. Invocation will throw SkillNotFoundException
            // because the dispatchers map stays empty; that path is covered
            // by GetAuthenticatedUserSkillTests.
            "github.get_authenticated_user",
        }, ignoreOrder: true);
    }
}
