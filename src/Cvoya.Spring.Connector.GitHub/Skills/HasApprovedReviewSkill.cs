// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Returns whether a pull request has at least one approving review.
/// GitHub's review history is append-only: a single reviewer can approve,
/// then request changes, then approve again — only the most recent state
/// per reviewer counts for "is this PR approved?". This skill honors that
/// by keeping the latest review per reviewer and checking for
/// <see cref="PullRequestReviewState.Approved"/>. This is the v1
/// <c>has_approved_review</c> helper used by the merge-gate logic.
/// </summary>
public class HasApprovedReviewSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HasApprovedReviewSkill>();

    /// <summary>
    /// Checks whether the specified PR has at least one approving review.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="requiredReviewer">
    /// Optional GitHub login. If specified, only that reviewer's latest state counts —
    /// the result is true iff that reviewer's most recent review is an approval.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element with <c>approved</c> and the set of approving reviewers.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string? requiredReviewer,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Checking approval status on {Owner}/{Repo}#{Number} requiredReviewer={Reviewer}",
            owner, repo, number, requiredReviewer ?? "*");

        var reviews = await gitHubClient.PullRequest.Review.GetAll(owner, repo, number);

        // Latest review per reviewer login wins.
        var latestByReviewer = reviews
            .Where(r => r.User?.Login != null)
            .GroupBy(r => r.User!.Login)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.SubmittedAt).Last());

        var approvers = latestByReviewer
            .Where(kvp => string.Equals(kvp.Value.State.StringValue, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        bool approved;
        if (!string.IsNullOrWhiteSpace(requiredReviewer))
        {
            approved = latestByReviewer.TryGetValue(requiredReviewer, out var review)
                && string.Equals(review.State.StringValue, "APPROVED", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            approved = approvers.Length > 0;
        }

        return JsonSerializer.SerializeToElement(new
        {
            approved,
            approvers,
            review_count = latestByReviewer.Count,
        });
    }
}
