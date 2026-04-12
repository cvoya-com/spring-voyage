// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Skills;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches GitHub tool invocations for the multi-turn tool-use loop. Parses the tool
/// input against the tool's JSON schema, resolves an authenticated Octokit client via
/// <see cref="GitHubConnector.CreateAuthenticatedClientAsync"/>, and delegates to the
/// concrete skill class registered for the tool name. Failures surface as
/// <see cref="ToolResult.IsError"/> rather than exceptions so the tool-use loop can continue.
/// </summary>
public class GitHubSkillToolExecutor : ISkillToolExecutor
{
    /// <summary>
    /// The prefix shared by every GitHub connector tool name.
    /// </summary>
    public const string ToolNamePrefix = "github_";

    private readonly GitHubConnector _connector;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="GitHubSkillToolExecutor"/>.
    /// </summary>
    /// <param name="connector">The GitHub connector used to mint authenticated clients.</param>
    /// <param name="loggerFactory">Factory used to create the executor's and per-skill loggers.</param>
    public GitHubSkillToolExecutor(GitHubConnector connector, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _connector = connector;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GitHubSkillToolExecutor>();
    }

    /// <inheritdoc />
    public bool CanHandle(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return toolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        _logger.LogInformation(
            "Dispatching GitHub tool '{ToolName}' (id {ToolUseId}).",
            call.Name, call.Id);

        try
        {
            var client = await _connector.CreateAuthenticatedClientAsync(cancellationToken);
            var payload = await DispatchAsync(client, call, cancellationToken);
            return new ToolResult(call.Id, payload.GetRawText(), IsError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "GitHub tool '{ToolName}' (id {ToolUseId}) failed: {Message}",
                call.Name, call.Id, ex.Message);
            return new ToolResult(
                call.Id,
                $"GitHub tool '{call.Name}' failed: {ex.Message}",
                IsError: true);
        }
    }

    private Task<JsonElement> DispatchAsync(
        Octokit.IGitHubClient client,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        return call.Name switch
        {
            "github_create_branch" => ExecuteCreateBranchAsync(client, call.Input, cancellationToken),
            "github_create_pull_request" => ExecuteCreatePullRequestAsync(client, call.Input, cancellationToken),
            "github_comment" => ExecuteCommentAsync(client, call.Input, cancellationToken),
            "github_read_file" => ExecuteReadFileAsync(client, call.Input, cancellationToken),
            "github_list_files" => ExecuteListFilesAsync(client, call.Input, cancellationToken),
            "github_get_issue_details" => ExecuteGetIssueDetailsAsync(client, call.Input, cancellationToken),
            "github_get_pull_request_diff" => ExecuteGetPullRequestDiffAsync(client, call.Input, cancellationToken),
            "github_manage_labels" => ExecuteManageLabelsAsync(client, call.Input, cancellationToken),
            _ => throw new ArgumentException($"Unknown GitHub tool '{call.Name}'.", nameof(call)),
        };
    }

    private Task<JsonElement> ExecuteCreateBranchAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var branchName = RequireString(input, "branchName");
        var fromRef = RequireString(input, "fromRef");

        return new CreateBranchSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, branchName, fromRef, cancellationToken);
    }

    private Task<JsonElement> ExecuteCreatePullRequestAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var title = RequireString(input, "title");
        var body = RequireString(input, "body");
        var head = RequireString(input, "head");
        var baseBranch = RequireString(input, "base");

        return new CreatePullRequestSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, title, body, head, baseBranch, cancellationToken);
    }

    private Task<JsonElement> ExecuteCommentAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var number = RequireInt(input, "number");
        var body = RequireString(input, "body");

        return new CommentSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, number, body, cancellationToken);
    }

    private Task<JsonElement> ExecuteReadFileAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var path = RequireString(input, "path");
        var gitRef = OptionalString(input, "ref");

        return new ReadFileSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, path, gitRef, cancellationToken);
    }

    private Task<JsonElement> ExecuteListFilesAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var path = RequireString(input, "path");
        var gitRef = OptionalString(input, "ref");

        return new ListFilesSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, path, gitRef, cancellationToken);
    }

    private Task<JsonElement> ExecuteGetIssueDetailsAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var number = RequireInt(input, "number");

        return new GetIssueDetailsSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, number, cancellationToken);
    }

    private Task<JsonElement> ExecuteGetPullRequestDiffAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var number = RequireInt(input, "number");

        return new GetPullRequestDiffSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, number, cancellationToken);
    }

    private Task<JsonElement> ExecuteManageLabelsAsync(
        Octokit.IGitHubClient client,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var owner = RequireString(input, "owner");
        var repo = RequireString(input, "repo");
        var number = RequireInt(input, "number");
        var labelsToAdd = OptionalStringArray(input, "labelsToAdd");
        var labelsToRemove = OptionalStringArray(input, "labelsToRemove");

        return new ManageLabelsSkill(client, _loggerFactory)
            .ExecuteAsync(owner, repo, number, labelsToAdd, labelsToRemove, cancellationToken);
    }

    private static string RequireString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"Required string property '{propertyName}' is missing from the tool input.",
                nameof(input));
        }

        return element.GetString() ?? throw new ArgumentException(
            $"Required string property '{propertyName}' was null.",
            nameof(input));
    }

    private static int RequireInt(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException(
                $"Required integer property '{propertyName}' is missing from the tool input.",
                nameof(input));
        }

        return element.GetInt32();
    }

    private static string? OptionalString(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static string[] OptionalStringArray(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (value is not null)
                {
                    values.Add(value);
                }
            }
        }

        return values.ToArray();
    }
}