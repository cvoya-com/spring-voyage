// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Requests reviews from users and/or teams on an open pull request.
/// </summary>
public class RequestPullRequestReviewSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RequestPullRequestReviewSkill>();

    /// <summary>
    /// Requests reviews on the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="reviewers">GitHub logins to request reviews from. May be empty.</param>
    /// <param name="teamReviewers">Team slugs to request reviews from. May be empty.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element listing the requested reviewers.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string[] reviewers,
        string[] teamReviewers,
        CancellationToken cancellationToken = default)
    {
        if (reviewers.Length == 0 && teamReviewers.Length == 0)
        {
            throw new ArgumentException("At least one of 'reviewers' or 'teamReviewers' must be non-empty.");
        }

        _logger.LogInformation(
            "Requesting reviews on {Owner}/{Repo}#{Number}: users=[{Users}] teams=[{Teams}]",
            owner, repo, number,
            string.Join(", ", reviewers),
            string.Join(", ", teamReviewers));

        var request = new PullRequestReviewRequest(reviewers, teamReviewers);
        var pr = await gitHubClient.PullRequest.ReviewRequest.Create(owner, repo, number, request);

        return JsonSerializer.SerializeToElement(new
        {
            number = pr.Number,
            html_url = pr.HtmlUrl,
            requested_reviewers = pr.RequestedReviewers?.Select(r => r.Login).ToArray() ?? [],
            requested_teams = pr.RequestedTeams?.Select(t => t.Slug).ToArray() ?? [],
        });
    }
}
