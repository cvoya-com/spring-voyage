// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Labels;
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
    private readonly LabelStateMachine _labelStateMachine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly Dictionary<string, Func<IGitHubClient, JsonElement, CancellationToken, Task<JsonElement>>> _dispatchers;

    /// <summary>
    /// Initializes the registry with the GitHub connector used to authenticate
    /// outbound Octokit calls, the configured <see cref="LabelStateMachine"/>
    /// (used by the label-transition skill), and a logger factory for per-skill
    /// loggers.
    /// </summary>
    public GitHubSkillRegistry(
        GitHubConnector connector,
        LabelStateMachine labelStateMachine,
        ILoggerFactory loggerFactory)
    {
        _connector = connector;
        _labelStateMachine = labelStateMachine;
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

            ["github_create_issue"] = (client, args, ct) =>
                new CreateIssueSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "title"),
                    GetOptionalString(args, "body"),
                    GetStringArray(args, "labels"),
                    GetStringArray(args, "assignees"),
                    ct),

            ["github_close_issue"] = (client, args, ct) =>
                new CloseIssueSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalString(args, "reason"),
                    ct),

            ["github_list_issues"] = (client, args, ct) =>
                new ListIssuesSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetOptionalString(args, "state"),
                    GetStringArray(args, "labels"),
                    GetOptionalString(args, "assignee"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_assign_issue"] = (client, args, ct) =>
                new AssignIssueSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetStringArray(args, "assigneesToAdd"),
                    GetStringArray(args, "assigneesToRemove"),
                    ct),

            ["github_get_issue_author"] = (client, args, ct) =>
                new GetIssueAuthorSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_update_comment"] = (client, args, ct) =>
                new UpdateCommentSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetLong(args, "commentId"),
                    GetString(args, "body"),
                    ct),

            ["github_list_comments"] = (client, args, ct) =>
                new ListCommentsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_get_pull_request"] = (client, args, ct) =>
                new GetPullRequestSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_list_pull_requests"] = (client, args, ct) =>
                new ListPullRequestsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetOptionalString(args, "state"),
                    GetOptionalString(args, "head"),
                    GetOptionalString(args, "base"),
                    GetOptionalString(args, "sort"),
                    GetOptionalString(args, "direction"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_find_pull_request_for_branch"] = (client, args, ct) =>
                new FindPullRequestForBranchSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "branch"),
                    GetOptionalString(args, "headOwner"),
                    GetOptionalBool(args, "includeClosed") ?? false,
                    ct),

            ["github_list_pull_requests_by_author"] = (client, args, ct) =>
                new ListPullRequestsByUserSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "login"),
                    ListPullRequestsByUserSkill.UserRole.Author,
                    GetOptionalString(args, "state"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_list_pull_requests_by_assignee"] = (client, args, ct) =>
                new ListPullRequestsByUserSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "login"),
                    ListPullRequestsByUserSkill.UserRole.Assignee,
                    GetOptionalString(args, "state"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_list_pull_request_reviews"] = (client, args, ct) =>
                new ListPullRequestReviewsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_list_pull_request_review_comments"] = (client, args, ct) =>
                new ListPullRequestReviewCommentsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalInt(args, "maxResults") ?? 30,
                    ct),

            ["github_has_approved_review"] = (client, args, ct) =>
                new HasApprovedReviewSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalString(args, "requiredReviewer"),
                    ct),

            ["github_merge_pull_request"] = (client, args, ct) =>
                new MergePullRequestSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalString(args, "mergeMethod"),
                    GetOptionalString(args, "commitTitle"),
                    GetOptionalString(args, "commitMessage"),
                    GetOptionalString(args, "sha"),
                    ct),

            ["github_enable_auto_merge"] = (client, args, ct) =>
                new EnableAutoMergeSkill(client, CreateGraphQLClient(client), _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalString(args, "mergeMethod"),
                    GetOptionalString(args, "commitHeadline"),
                    GetOptionalString(args, "commitBody"),
                    ct),

            ["github_list_review_threads"] = (client, args, ct) =>
                new ListReviewThreadsSkill(CreateGraphQLClient(client), _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    ct),

            ["github_resolve_review_thread"] = (client, args, ct) =>
                new ResolveReviewThreadSkill(CreateGraphQLClient(client), _loggerFactory).ExecuteAsync(
                    GetString(args, "threadId"),
                    ct),

            ["github_unresolve_review_thread"] = (client, args, ct) =>
                new UnresolveReviewThreadSkill(CreateGraphQLClient(client), _loggerFactory).ExecuteAsync(
                    GetString(args, "threadId"),
                    ct),

            ["github_update_branch"] = (client, args, ct) =>
                new UpdateBranchSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetOptionalString(args, "expectedHeadSha"),
                    ct),

            ["github_request_pull_request_review"] = (client, args, ct) =>
                new RequestPullRequestReviewSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetStringArray(args, "reviewers"),
                    GetStringArray(args, "teamReviewers"),
                    ct),

            ["github_ensure_issue_linked_to_pull_request"] = (client, args, ct) =>
                new EnsureIssueLinkedToPullRequestSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetIntArray(args, "issueNumbers"),
                    ct),

            ["github_search_mentions"] = (client, args, ct) =>
                new SearchMentionsSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "user"),
                    GetOptionalDateTimeOffset(args, "since"),
                    GetOptionalInt(args, "limit") ?? 30,
                    ct),

            ["github_get_prior_work_context"] = (client, args, ct) =>
                new GetPriorWorkContextSkill(client, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetString(args, "user"),
                    GetOptionalDateTimeOffset(args, "since"),
                    GetOptionalInt(args, "maxPerBucket") ?? 20,
                    ct),

            ["github_label_transition"] = (client, args, ct) =>
                new LabelTransitionSkill(client, _labelStateMachine, _loggerFactory).ExecuteAsync(
                    GetString(args, "owner"),
                    GetString(args, "repo"),
                    GetInt(args, "number"),
                    GetString(args, "toState"),
                    ct),
        };
    }

    /// <summary>
    /// Creates a GraphQL client that shares the authenticated connection
    /// of the per-call Octokit client. Kept internal to the registry so
    /// each skill dispatcher reuses the same factory — tests can construct
    /// <see cref="OctokitGraphQLClient"/> directly against a mocked
    /// <see cref="IConnection"/>.
    /// </summary>
    private IGitHubGraphQLClient CreateGraphQLClient(IGitHubClient client)
        => new OctokitGraphQLClient(client.Connection, _loggerFactory);

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

    private static int? GetOptionalInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            return null;
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

    private static long GetLong(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Missing or non-integer argument '{name}'.");
        }
        return prop.GetInt64();
    }

    private static bool? GetOptionalBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
        {
            return null;
        }
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static int[] GetIntArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return prop.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetInt32())
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
                }),

            CreateToolDefinition(
                "github_create_issue",
                "Creates a new issue in a GitHub repository.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        title = new { type = "string", description = "The issue title" },
                        body = new { type = "string", description = "The issue body / description" },
                        labels = new { type = "array", items = new { type = "string" }, description = "Labels to apply on creation" },
                        assignees = new { type = "array", items = new { type = "string" }, description = "GitHub logins to assign on creation" }
                    },
                    required = new[] { "owner", "repo", "title" }
                }),

            CreateToolDefinition(
                "github_close_issue",
                "Closes an existing GitHub issue.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue number" },
                        reason = new { type = "string", description = "Optional close reason: completed, not_planned, or reopened" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_list_issues",
                "Lists issues in a GitHub repository filtered by state, labels, or assignee. Pull requests are excluded.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        state = new { type = "string", description = "State filter: open (default), closed, or all" },
                        labels = new { type = "array", items = new { type = "string" }, description = "Labels to filter by (logical AND)" },
                        assignee = new { type = "string", description = "Assignee login filter (* for any, none for unassigned)" },
                        maxResults = new { type = "integer", description = "Maximum issues to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo" }
                }),

            CreateToolDefinition(
                "github_assign_issue",
                "Adds and/or removes assignees on a GitHub issue or pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue or PR number" },
                        assigneesToAdd = new { type = "array", items = new { type = "string" }, description = "GitHub logins to add as assignees" },
                        assigneesToRemove = new { type = "array", items = new { type = "string" }, description = "GitHub logins to remove as assignees" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_get_issue_author",
                "Gets the login of the user who opened a GitHub issue.",
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
                "github_update_comment",
                "Updates the body of an existing issue or pull request conversation comment.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        commentId = new { type = "integer", description = "The numeric id of the comment to update" },
                        body = new { type = "string", description = "The replacement comment body text" }
                    },
                    required = new[] { "owner", "repo", "commentId", "body" }
                }),

            CreateToolDefinition(
                "github_list_comments",
                "Lists conversation comments on an issue or pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue or pull request number" },
                        maxResults = new { type = "integer", description = "Maximum comments to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_get_pull_request",
                "Gets detailed information about a GitHub pull request.",
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
                "github_list_pull_requests",
                "Lists pull requests in a repository filtered by state, head branch, base branch, with optional sort.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        state = new { type = "string", description = "State filter: open (default), closed, or all" },
                        head = new { type = "string", description = "Optional head filter in user:branch form" },
                        @base = new { type = "string", description = "Optional base branch filter" },
                        sort = new { type = "string", description = "Sort key: created (default), updated, popularity, long-running" },
                        direction = new { type = "string", description = "Sort direction: asc or desc (default)" },
                        maxResults = new { type = "integer", description = "Maximum pull requests to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo" }
                }),

            CreateToolDefinition(
                "github_find_pull_request_for_branch",
                "Finds the pull request associated with a given branch, if one exists.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        branch = new { type = "string", description = "The head branch name (without user: prefix)" },
                        headOwner = new { type = "string", description = "Optional owner of the branch head; defaults to owner" },
                        includeClosed = new { type = "boolean", description = "Whether to include closed pull requests" }
                    },
                    required = new[] { "owner", "repo", "branch" }
                }),

            CreateToolDefinition(
                "github_list_pull_requests_by_author",
                "Lists pull requests opened by the specified GitHub user in a repository, via the Search API.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        login = new { type = "string", description = "The GitHub login of the author" },
                        state = new { type = "string", description = "State filter: open (default), closed, or all" },
                        maxResults = new { type = "integer", description = "Maximum pull requests to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo", "login" }
                }),

            CreateToolDefinition(
                "github_list_pull_requests_by_assignee",
                "Lists pull requests assigned to the specified GitHub user in a repository, via the Search API.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        login = new { type = "string", description = "The GitHub login of the assignee" },
                        state = new { type = "string", description = "State filter: open (default), closed, or all" },
                        maxResults = new { type = "integer", description = "Maximum pull requests to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo", "login" }
                }),

            CreateToolDefinition(
                "github_list_pull_request_reviews",
                "Lists the reviews submitted on a pull request.",
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
                "github_list_pull_request_review_comments",
                "Lists the line-level review comments on a pull request's diff.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        maxResults = new { type = "integer", description = "Maximum comments to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_has_approved_review",
                "Returns whether a pull request has at least one approving review (most recent state per reviewer).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        requiredReviewer = new { type = "string", description = "Optional GitHub login whose latest review must be an approval" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_merge_pull_request",
                "Merges a pull request using the specified strategy (merge, squash, or rebase).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        mergeMethod = new { type = "string", description = "Merge strategy: merge (default), squash, or rebase" },
                        commitTitle = new { type = "string", description = "Optional merge commit title" },
                        commitMessage = new { type = "string", description = "Optional merge commit message" },
                        sha = new { type = "string", description = "Optional SHA the PR head must match" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_enable_auto_merge",
                "Enables auto-merge on a pull request (via the GraphQL enablePullRequestAutoMerge mutation).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        mergeMethod = new { type = "string", description = "Merge strategy when auto-merging: merge, squash (default), or rebase" },
                        commitHeadline = new { type = "string", description = "Optional commit headline" },
                        commitBody = new { type = "string", description = "Optional commit body" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_update_branch",
                "Updates a pull request branch by merging the base branch into it (PUT /pulls/:n/update-branch).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        expectedHeadSha = new { type = "string", description = "Optional expected head SHA the PR must still be at" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_request_pull_request_review",
                "Requests reviews from users and/or teams on an open pull request.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        reviewers = new { type = "array", items = new { type = "string" }, description = "GitHub logins to request reviews from" },
                        teamReviewers = new { type = "array", items = new { type = "string" }, description = "Team slugs to request reviews from" }
                    },
                    required = new[] { "owner", "repo", "number" }
                }),

            CreateToolDefinition(
                "github_ensure_issue_linked_to_pull_request",
                "Ensures a pull request body contains closing-keyword references for each of the given issue numbers, appending Closes #N lines when missing.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The pull request number" },
                        issueNumbers = new { type = "array", items = new { type = "integer" }, description = "Issue numbers that should be auto-closed by this PR" }
                    },
                    required = new[] { "owner", "repo", "number", "issueNumbers" }
                }),

            CreateToolDefinition(
                "github_search_mentions",
                "Searches a repository for @-mentions of the given GitHub login in issues, pull requests, and their comments.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        user = new { type = "string", description = "The GitHub login to search mentions for (with or without leading @)" },
                        since = new { type = "string", description = "Optional ISO-8601 lower bound; only include items updated after this timestamp" },
                        limit = new { type = "integer", description = "Maximum number of mentions to return (capped at 100)" }
                    },
                    required = new[] { "owner", "repo", "user" }
                }),

            CreateToolDefinition(
                "github_get_prior_work_context",
                "Summarizes recent agent activity for a GitHub login in a repository: mentions, authored PRs, commented issues, and assigned issues.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        user = new { type = "string", description = "The GitHub login whose prior work should be summarized" },
                        since = new { type = "string", description = "Optional ISO-8601 lower bound; only include items updated after this timestamp" },
                        maxPerBucket = new { type = "integer", description = "Maximum items per bucket (mentions / authored / commented / assigned); capped at 100" }
                    },
                    required = new[] { "owner", "repo", "user" }
                }),

            CreateToolDefinition(
                "github_label_transition",
                "Transitions an issue or pull request to the given state label, validated against the configured label state machine. Rejects illegal transitions.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "The repository owner" },
                        repo = new { type = "string", description = "The repository name" },
                        number = new { type = "integer", description = "The issue or pull request number" },
                        toState = new { type = "string", description = "The destination state label; must be in the configured state set" }
                    },
                    required = new[] { "owner", "repo", "number", "toState" }
                }),

            CreateToolDefinition(
                "github_list_review_threads",
                "Lists review threads on a pull request via GraphQL, including per-thread resolution state (which the REST API does not expose). Returns an is_resolved flag per thread and a summary unresolved_count.",
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
                "github_resolve_review_thread",
                "Marks a pull request review thread as resolved via the GraphQL resolveReviewThread mutation. Idempotent on an already-resolved thread.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        threadId = new { type = "string", description = "The GraphQL node id of the review thread (returned by github_list_review_threads)" }
                    },
                    required = new[] { "threadId" }
                }),

            CreateToolDefinition(
                "github_unresolve_review_thread",
                "Reopens a previously resolved pull request review thread via the GraphQL unresolveReviewThread mutation. Idempotent on an already-unresolved thread.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        threadId = new { type = "string", description = "The GraphQL node id of the review thread" }
                    },
                    required = new[] { "threadId" }
                })
        ];
    }

    private static ToolDefinition CreateToolDefinition(string name, string description, object schema)
    {
        var schemaElement = JsonSerializer.SerializeToElement(schema);
        return new ToolDefinition(name, description, schemaElement);
    }
}