// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using Octokit;

/// <summary>
/// Shared projection helpers for pull-request skill responses.
/// Keeping the shape consistent across <c>github_get_pull_request</c>,
/// <c>github_list_pull_requests</c>, and related query skills makes
/// downstream planning logic simpler because every PR response has
/// the same field names and types.
/// </summary>
internal static class PullRequestProjection
{
    public static object ProjectSummary(PullRequest pr) => new
    {
        number = pr.Number,
        title = pr.Title,
        state = pr.State.StringValue,
        draft = pr.Draft,
        merged = pr.Merged,
        html_url = pr.HtmlUrl,
        head = pr.Head?.Ref,
        head_sha = pr.Head?.Sha,
        @base = pr.Base?.Ref,
        author = pr.User?.Login,
        assignees = pr.Assignees?.Select(a => a.Login).ToArray() ?? [],
        requested_reviewers = pr.RequestedReviewers?.Select(a => a.Login).ToArray() ?? [],
        labels = pr.Labels?.Select(l => l.Name).ToArray() ?? [],
        created_at = pr.CreatedAt,
        updated_at = pr.UpdatedAt,
        merged_at = pr.MergedAt,
        closed_at = pr.ClosedAt,
    };

    public static object ProjectDetail(PullRequest pr) => new
    {
        number = pr.Number,
        title = pr.Title,
        body = pr.Body,
        state = pr.State.StringValue,
        draft = pr.Draft,
        merged = pr.Merged,
        mergeable = pr.Mergeable,
        mergeable_state = pr.MergeableState?.StringValue,
        html_url = pr.HtmlUrl,
        head = pr.Head?.Ref,
        head_sha = pr.Head?.Sha,
        @base = pr.Base?.Ref,
        author = pr.User?.Login,
        assignees = pr.Assignees?.Select(a => a.Login).ToArray() ?? [],
        requested_reviewers = pr.RequestedReviewers?.Select(a => a.Login).ToArray() ?? [],
        labels = pr.Labels?.Select(l => l.Name).ToArray() ?? [],
        additions = pr.Additions,
        deletions = pr.Deletions,
        changed_files = pr.ChangedFiles,
        commits = pr.Commits,
        comments = pr.Comments,
        created_at = pr.CreatedAt,
        updated_at = pr.UpdatedAt,
        merged_at = pr.MergedAt,
        closed_at = pr.ClosedAt,
    };
}
