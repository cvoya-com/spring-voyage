// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists the line-level review comments left on a pull request's diff.
/// This is distinct from conversation comments, which are covered by
/// <see cref="ListCommentsSkill"/>.
/// </summary>
public class ListPullRequestReviewCommentsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListPullRequestReviewCommentsSkill>();

    /// <summary>
    /// Lists the review (line-level) comments on the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="maxResults">Maximum number of comments to return. Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the line-level comments.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var options = new ApiOptions
        {
            PageSize = Math.Clamp(maxResults, 1, 100),
            PageCount = 1,
        };

        _logger.LogInformation(
            "Listing review comments on {Owner}/{Repo}#{Number} (max {Max})",
            owner, repo, number, options.PageSize);

        var comments = await gitHubClient.PullRequest.ReviewComment.GetAll(owner, repo, number, options);

        var projected = comments.Select(c => new
        {
            id = c.Id,
            body = c.Body,
            path = c.Path,
            position = c.Position,
            original_position = c.OriginalPosition,
            diff_hunk = c.DiffHunk,
            commit_id = c.CommitId,
            author = c.User?.Login,
            html_url = c.HtmlUrl,
            created_at = c.CreatedAt,
            updated_at = c.UpdatedAt,
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { review_comments = projected, count = projected.Length });
    }
}
