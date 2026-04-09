/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;
using FluentAssertions;
using Xunit;

public class GitHubSkillRegistryTests
{
    private readonly GitHubSkillRegistry _registry = new();

    [Fact]
    public void GetToolDefinitions_ReturnsAllTools()
    {
        var tools = _registry.GetToolDefinitions();

        tools.Should().HaveCount(8);
        tools.Select(t => t.Name).Should().Contain([
            "github_create_branch",
            "github_create_pull_request",
            "github_comment",
            "github_read_file",
            "github_list_files",
            "github_get_issue_details",
            "github_get_pull_request_diff",
            "github_manage_labels"
        ]);
    }

    [Fact]
    public void GetToolDefinitions_AllHaveValidJsonSchemas()
    {
        var tools = _registry.GetToolDefinitions();

        foreach (var tool in tools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
            tool.InputSchema.GetProperty("type").GetString().Should().Be("object");
            tool.InputSchema.TryGetProperty("properties", out _).Should().BeTrue();
        }
    }
}
