// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Updates the body of an existing issue or pull-request conversation comment.
/// GitHub uses the same Issue Comments API for both surfaces, so a single
/// implementation covers both cases — the caller supplies the numeric comment id.
/// Line-level PR review comments live on a different endpoint and are not handled here.
/// </summary>
public class UpdateCommentSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpdateCommentSkill>();

    /// <summary>
    /// Updates the body text of the specified issue / PR comment.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="commentId">The numeric id of the comment to update.</param>
    /// <param name="body">The replacement comment body text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the updated comment metadata.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        long commentId,
        string body,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Updating comment {CommentId} on {Owner}/{Repo}",
            commentId, owner, repo);

        var comment = await gitHubClient.Issue.Comment.Update(owner, repo, commentId, body);

        var result = new
        {
            id = comment.Id,
            html_url = comment.HtmlUrl,
            updated_at = comment.UpdatedAt,
            body = comment.Body,
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
