// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists reviews on a pull request.
/// </summary>
public class ListPullRequestReviewsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListPullRequestReviewsSkill>();

    /// <summary>
    /// Lists the reviews submitted on the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the review list.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Listing reviews on {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var reviews = await gitHubClient.PullRequest.Review.GetAll(owner, repo, number);

        var projected = reviews.Select(r => new
        {
            id = r.Id,
            state = r.State.StringValue,
            body = r.Body,
            reviewer = r.User?.Login,
            commit_id = r.CommitId,
            html_url = r.HtmlUrl,
            submitted_at = r.SubmittedAt,
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { reviews = projected, count = projected.Length });
    }
}
