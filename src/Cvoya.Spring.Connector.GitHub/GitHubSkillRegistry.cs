// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Registers all GitHub connector skills and provides their tool definitions
/// for discovery by agents.
/// </summary>
public class GitHubSkillRegistry
{
    private readonly List<ToolDefinition> _tools;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubSkillRegistry"/> class
    /// with all GitHub tool definitions pre-registered.
    /// </summary>
    public GitHubSkillRegistry()
    {
        _tools =
        [
            CreateToolDefinition(
                "github_create_branch",
                "Creates a new Git branch in a GitHub repository from a specified reference.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        branchName = new { type = "string", description = "The name of the new branch" },
                        fromRef = new { type = "string", description = "The reference (branch or SHA) to branch from" }
                    },
                    required = new[] { "owner", "repo", "branchName", "fromRef" }
                }),

            CreateToolDefinition(
                "github_create_pull_request",
                "Creates a pull request in a GitHub repository.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        title = new { type = "string", description = "The pull request title" },
                        body = new { type = "string", description = "The pull request body/description" },
                        head = new { type = "string", description = "The head branch containing the changes" },
                        @base = new { type = "string", description = "The base branch to merge into" }
                    },
                    required = new[] { "owner", "repo", "title", "body", "head", "base" }
                }),

            CreateToolDefinition(
                "github_comment",
                "Creates a comment on a GitHub issue or pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue or PR number" },
                        body = new { type = "string", description = "The comment body text" }
                    },
                    required = new[] { "owner", "repo", "number", "body" }
                }),

            CreateToolDefinition(
                "github_read_file",
                "Reads a file from a GitHub repository.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        path = new { type = "string", description = "The file path within the repository" },
                        @ref = new { type = "string", description = "Optional Git reference (branch, tag, or SHA)" }
                    },
                    required = new[] { "owner", "repo", "path" }
                }),

            CreateToolDefinition(
                "github_list_files",
                "Lists files in a directory within a GitHub repository.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        path = new { type = "string", description = "The directory path within the repository" },
                        @ref = new { type = "string", description = "Optional Git reference (branch, tag, or SHA)" }
                    },
                    required = new[] { "owner", "repo", "path" }
                }),

            CreateToolDefinition(
                "github_get_issue_details",
                "Gets detailed information about a GitHub issue.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue number" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_get_pull_request_diff",
                "Gets the file changes (diff) for a GitHub pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_manage_labels",
                "Adds and/or removes labels on a GitHub issue or pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue or PR number" },
                        labelsToAdd = new { type = "array", items = new { type = "string" }, description = "Labels to add" },
                        labelsToRemove = new { type = "array", items = new { type = "string" }, description = "Labels to remove" }
                    },
                    required = new[] { "owner", "repo", "number" }
                })
        ];
    }

    /// <summary>
    /// Gets all registered GitHub tool definitions.
    /// </summary>
    /// <returns>A read-only list of tool definitions.</returns>
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools.AsReadOnly();

    private static ToolDefinition CreateToolDefinition(string name, string description, object schema)
    {
        var schemaElement = JsonSerializer.SerializeToElement(schema);
        return new ToolDefinition(name, description, schemaElement);
    }
}