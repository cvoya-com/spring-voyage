// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
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
        var connector = new GitHubConnector(auth, webhookHandler, signatureValidator, options, loggerFactory);
        _registry = new GitHubSkillRegistry(connector, loggerFactory);
    }

    [Fact]
    public void GetToolDefinitions_ReturnsAllTools()
    {
        var tools = _registry.GetToolDefinitions();

        tools.Count().ShouldBe(16);
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