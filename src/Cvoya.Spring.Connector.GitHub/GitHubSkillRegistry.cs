// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Skills;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Registers all GitHub connector tool definitions and invokes them by name,
/// authenticating the underlying <see cref="IGitHubClient"/> lazily per call via
/// <see cref="GitHubConnector.CreateAuthenticatedClientAsync"/>. Implements
/// <see cref="ISkillRegistry"/> so the MCP server (and any future planner) can
/// discover and dispatch GitHub tools through a single abstraction.
/// </summary>
public class GitHubSkillRegistry : ISkillRegistry
{
    private readonly GitHubConnector _connector;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly Dictionary<string, Func<IGitHubClient, JsonElement, CancellationToken, Task<JsonElement>>> _dispatchers;

    /// <summary>
    /// Initializes the registry with the GitHub connector used to authenticate
    /// outbound Octokit calls and a logger factory for per-skill loggers.
    /// </summary>
    public GitHubSkillRegistry(GitHubConnector connector, ILoggerFactory loggerFactory)
    {
        _connector = connector;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GitHubSkillRegistry>();

        _tools = BuildToolDefinitions();
        _dispatchers = BuildDispatchers();
    }

    /// <inheritdoc />
    public string Name => "github";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_dispatchers.TryGetValue(toolName, out var dispatch))
        {
            throw new SkillNotFoundException(toolName);
        }

        _logger.LogInformation("Invoking GitHub skill {ToolName}", toolName);
        var client = await _connector.CreateAuthenticatedClientAsync(cancellationToken);
        return await dispatch(client, arguments, cancellationToken);
    }

    private Dictionary<string, Func<IGitHubClient, JsonElement, CancellationToken, Task<JsonElement>>> BuildDispatchers()
    {
        return new Dictionary<string, Func<IGitHubClient, JsonElement, CancellationToken, Task<JsonElement>>>(StringComparer.Ordinal)
        {
            ["github_create_branch"] = (client, args, ct) =>
                new CreateBranchSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "branchName"),
                    GetString(args, "fromRef"),
                    ct),

            ["github_create_pull_request"] = (client, args, ct) =>
                new CreatePullRequestSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "title"),
                    GetString(args, "body"),
                    GetString(args, "head"),
                    GetString(args, "base"),
                    ct),

            ["github_comment_on_issue"] = (client, args, ct) =>
                new CommentSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetString(args, "body"),
                    "issue",
                    ct),

            ["github_comment_on_pull_request"] = (client, args, ct) =>
                new CommentSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetString(args, "body"),
                    "pull_request",
                    ct),

            ["github_read_file"] = (client, args, ct) =>
                new ReadFileSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "path"),
                    GetOptionalString(args, "ref"),
                    ct),

            ["github_write_file"] = (client, args, ct) =>
                new WriteFileSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "path"),
                    GetString(args, "content"),
                    GetString(args, "message"),
                    GetString(args, "branch"),
                    ct),

            ["github_delete_file"] = (client, args, ct) =>
                new DeleteFileSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "path"),
                    GetString(args, "message"),
                    GetString(args, "branch"),
                    ct),

            ["github_list_files"] = (client, args, ct) =>
                new ListFilesSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "path"),
                    GetOptionalString(args, "ref"),
                    ct),

            ["github_get_issue_details"] = (client, args, ct) =>
                new GetIssueDetailsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_get_pull_request_diff"] = (client, args, ct) =>
                new GetPullRequestDiffSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_manage_labels"] = (client, args, ct) =>
                new ManageLabelsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetStringArray(args, "labelsToAdd"),
                    GetStringArray(args, "labelsToRemove"),
                    ct),
        };
    }

    private static string GetString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing or non-string argument '{name}'.");
        }
        return prop.GetString()!;
    }

    private static string? GetOptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int GetInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Missing or non-integer argument '{name}'.");
        }
        return prop.GetInt32();
    }

    private static string[] GetStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return prop.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<ToolDefinition> BuildToolDefinitions()
    {
        return
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
                "github_comment_on_issue",
                "Posts a comment on a GitHub issue conversation thread.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue number" },
                        body = new { type = "string", description = "The comment body text" }
                    },
                    required = new[] { "owner", "repo", "number", "body" }
                }),

            CreateToolDefinition(
                "github_comment_on_pull_request",
                "Posts a comment on a GitHub pull request conversation thread. Does not place line-level review comments.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
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
                "github_write_file",
                "Creates or updates a file in a GitHub repository on the specified branch. If the file exists it is overwritten; otherwise it is created.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        path = new { type = "string", description = "The file path within the repository" },
                        content = new { type = "string", description = "The UTF-8 text contents to write" },
                        message = new { type = "string", description = "The commit message" },
                        branch = new { type = "string", description = "The branch to commit against" }
                    },
                    required = new[] { "owner", "repo", "path", "content", "message", "branch" }
                }),

            CreateToolDefinition(
                "github_delete_file",
                "Deletes a file from a GitHub repository on the specified branch.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        path = new { type = "string", description = "The file path within the repository" },
                        message = new { type = "string", description = "The commit message" },
                        branch = new { type = "string", description = "The branch to commit against" }
                    },
                    required = new[] { "owner", "repo", "path", "message", "branch" }
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

    private static ToolDefinition CreateToolDefinition(string name, string description, object schema)
    {
        var schemaElement = JsonSerializer.SerializeToElement(schema);
        return new ToolDefinition(name, description, schemaElement);
    }
}