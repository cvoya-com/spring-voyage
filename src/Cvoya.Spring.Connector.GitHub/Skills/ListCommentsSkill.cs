// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists conversation comments on an issue or pull request, oldest-first.
/// PR conversation comments share the same Issue Comments API as issue comments —
/// GitHub treats a PR number as an issue number for this endpoint.
/// </summary>
public class ListCommentsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListCommentsSkill>();

    /// <summary>
    /// Lists comments on the specified issue or pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue or pull-request number.</param>
    /// <param name="maxResults">Maximum number of comments to return. Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the comment collection.</returns>
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
            "Listing comments on {Owner}/{Repo}#{Number} (max {Max})",
            owner, repo, number, options.PageSize);

        var comments = await gitHubClient.Issue.Comment.GetAllForIssue(owner, repo, number, options);

        var projected = comments
            .Select(c => new
            {
                id = c.Id,
                body = c.Body,
                author = c.User?.Login,
                html_url = c.HtmlUrl,
                created_at = c.CreatedAt,
                updated_at = c.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.SerializeToElement(new { comments = projected, count = projected.Length });
    }
}
