// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Finds the pull request associated with a given branch. This is the v1
/// <c>find_pr_for_branch</c> helper used by the orchestrator to decide whether
/// to open a new PR or update an existing one when a branch is pushed.
/// Returns the first matching PR (open by default), or a <c>found = false</c>
/// result if none exists.
/// </summary>
public class FindPullRequestForBranchSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<FindPullRequestForBranchSkill>();

    /// <summary>
    /// Finds the PR for the given branch.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="branch">The head branch name (without <c>owner:</c> prefix).</param>
    /// <param name="headOwner">
    /// Optional owner of the branch head. Defaults to <paramref name="owner"/>.
    /// Supply a different value for cross-fork PRs.
    /// </param>
    /// <param name="includeClosed">Whether to include closed pull requests in the search. Defaults to false.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element with either <c>found=true</c> and the PR summary, or <c>found=false</c>.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string branch,
        string? headOwner,
        bool includeClosed,
        CancellationToken cancellationToken = default)
    {
        var headFilter = $"{headOwner ?? owner}:{branch}";

        _logger.LogInformation(
            "Searching PRs in {Owner}/{Repo} for head={Head} includeClosed={IncludeClosed}",
            owner, repo, headFilter, includeClosed);

        var filter = new PullRequestRequest
        {
            State = includeClosed ? ItemStateFilter.All : ItemStateFilter.Open,
            Head = headFilter,
            SortProperty = PullRequestSort.Updated,
            SortDirection = SortDirection.Descending,
        };

        var prs = await gitHubClient.PullRequest.GetAllForRepository(
            owner, repo, filter, new ApiOptions { PageSize = 10, PageCount = 1 });

        var match = prs.FirstOrDefault();
        if (match == null)
        {
            return JsonSerializer.SerializeToElement(new { found = false });
        }

        return JsonSerializer.SerializeToElement(new
        {
            found = true,
            pull_request = PullRequestProjection.ProjectSummary(match),
        });
    }
}
