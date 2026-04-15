// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Bundled PR-review fetcher — returns reviews, line-level review
/// comments, and review threads in a single GraphQL round-trip. Use
/// when a caller needs all three (typical PR-review turn); the three
/// individual skills remain for one-off callers.
/// </summary>
/// <remarks>
/// Response shape is a superset of the three individual skills' responses,
/// with per-section errors surfaced under <c>errors</c>. Cache integration
/// from D9 keys the bundle under the same <c>pr:&lt;owner&gt;/&lt;repo&gt;#&lt;number&gt;</c>
/// tag as the individual skills so a PR webhook invalidation clears all
/// four cache rows in one pass.
/// </remarks>
public class GetPrReviewBundleSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetPrReviewBundleSkill>();

    /// <summary>
    /// Executes the bundle. <paramref name="maxPerSection"/> is clamped
    /// to [1, 100] to respect GitHub's GraphQL connection caps.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        int maxPerSection,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(maxPerSection, 1, 100);

        _logger.LogInformation(
            "Fetching PR review bundle for {Owner}/{Repo}#{Number} (max {Max} per section) via GraphQL batch",
            owner, repo, number, clamped);

        var bundle = await PrReviewBundleBatch
            .ExecuteAsync(graphQLClient, owner, repo, number, clamped, cancellationToken)
            .ConfigureAwait(false);

        var reviews = bundle.Reviews.Select(r => new
        {
            id = r.DatabaseId,
            state = r.State,
            body = r.Body,
            reviewer = r.Author?.Login,
            commit_id = r.Commit?.Oid,
            html_url = r.Url,
            submitted_at = r.SubmittedAt,
        }).ToArray();

        // Flatten thread-keyed comments into a list matching the
        // shape of the pre-migration ListPullRequestReviewCommentsSkill
        // response so callers that read the bundle without a schema
        // change observe the same per-comment keys.
        var reviewComments = bundle.ReviewComments
            .Where(t => t.Comments?.Nodes is { Count: > 0 })
            .SelectMany(t => t.Comments!.Nodes, (thread, comment) => new
            {
                id = comment.DatabaseId,
                body = comment.Body,
                path = comment.Path ?? thread.Path,
                position = comment.Position,
                original_position = comment.OriginalPosition,
                diff_hunk = comment.DiffHunk,
                commit_id = comment.Commit?.Oid,
                author = comment.Author?.Login,
                html_url = comment.Url,
                created_at = comment.CreatedAt,
                updated_at = comment.UpdatedAt,
                thread_id = thread.ThreadId,
                is_resolved = thread.IsResolved,
            })
            .ToArray();

        var threads = bundle.ReviewThreads
            .Select(t => new
            {
                thread_id = t.Id,
                is_resolved = t.IsResolved,
                is_outdated = t.IsOutdated,
                path = t.Path,
                line = t.Line,
                comments = (t.Comments?.Nodes ?? [])
                    .Select(c => new
                    {
                        id = c.Id,
                        database_id = c.DatabaseId,
                        body = c.Body,
                        author = c.Author?.Login,
                    })
                    .ToArray(),
            })
            .ToArray();

        var unresolvedThreadCount = bundle.ReviewThreads.Count(t => !t.IsResolved);

        var payload = new
        {
            owner,
            repo,
            number,
            reviews = new { count = reviews.Length, items = reviews },
            review_comments = new { count = reviewComments.Length, items = reviewComments },
            review_threads = new
            {
                count = bundle.ReviewThreads.Count,
                unresolved_count = unresolvedThreadCount,
                has_unresolved_review_threads = unresolvedThreadCount > 0,
                items = threads,
            },
            errors = bundle.Errors,
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}