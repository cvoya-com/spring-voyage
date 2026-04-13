// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Creates a conversation-thread comment on a GitHub issue or pull request.
/// Both surfaces use GitHub's Issue Comments API — the PR conversation tab is
/// the issue comments endpoint — so a single implementation backs both
/// <c>github_comment_on_issue</c> and <c>github_comment_on_pull_request</c>.
/// Line-level review comments on PR diffs are a separate API and are not
/// handled here.
/// </summary>
public class CommentSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CommentSkill>();

    /// <summary>
    /// Creates a comment on the specified issue or pull request conversation thread.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue or pull request number.</param>
    /// <param name="body">The comment body text.</param>
    /// <param name="target">The target surface — <c>issue</c> or <c>pull_request</c> — used only for logging and the response payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the created comment details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string body,
        string target,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating {Target} comment on {Owner}/{Repo}#{Number}",
            target, owner, repo, number);

        var comment = await gitHubClient.Issue.Comment.Create(owner, repo, number, body);

        var result = new
        {
            id = comment.Id,
            target,
            html_url = comment.HtmlUrl,
            created_at = comment.CreatedAt
        };

        return JsonSerializer.SerializeToElement(result);
    }
}