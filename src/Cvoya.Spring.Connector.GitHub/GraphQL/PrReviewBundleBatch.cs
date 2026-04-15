// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Globalization;
using System.Text.Json.Serialization;

/// <summary>
/// Batches the three queries that back a PR-review turn — top-level
/// reviews, line-level review comments, and thread-level resolution
/// state — into a single GraphQL request. A PR-review turn that
/// previously dispatched three skills (and three HTTP round-trips) now
/// makes one.
/// </summary>
/// <remarks>
/// <para>
/// The three individual skills
/// (<c>github_list_pull_request_reviews</c>,
/// <c>github_list_pull_request_review_comments</c>,
/// <c>github_list_review_threads</c>) remain in place for callers that
/// only need one slice; the new <c>github_get_pr_review_bundle</c> skill
/// is an opt-in performance upgrade for callers that need all three.
/// </para>
/// <para>
/// Partial failure on any sub-query surfaces through a per-section
/// <see cref="PrReviewBundle.Errors"/> entry. The remaining sections
/// are returned normally.
/// </para>
/// </remarks>
public static class PrReviewBundleBatch
{
    /// <summary>Alias for the reviews section of the bundle.</summary>
    public const string ReviewsAlias = "reviews_pr";

    /// <summary>Alias for the line-level review-comments section.</summary>
    public const string ReviewCommentsAlias = "review_comments_pr";

    /// <summary>Alias for the review-threads section.</summary>
    public const string ReviewThreadsAlias = "review_threads_pr";

    /// <summary>
    /// Executes the PR-review bundle batch. <paramref name="maxPerSection"/>
    /// is clamped to [1, 100] by GitHub's GraphQL connection caps; the
    /// caller is responsible for any clamping before this call.
    /// </summary>
    public static async Task<PrReviewBundle> ExecuteAsync(
        IGitHubGraphQLClient client,
        string owner,
        string repo,
        int number,
        int maxPerSection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "number must be positive.");
        }
        if (maxPerSection < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerSection), "maxPerSection must be >= 1.");
        }

        var batch = new GraphQLBatch();
        batch.Add<PrReviewsNode>(ReviewsAlias, BuildReviewsBody(owner, repo, number, maxPerSection));
        batch.Add<PrReviewCommentsNode>(ReviewCommentsAlias, BuildReviewCommentsBody(owner, repo, number, maxPerSection));
        batch.Add<RepositoryWithPullRequest>(ReviewThreadsAlias, BuildReviewThreadsBody(owner, repo, number, maxPerSection));

        var result = await batch.ExecuteAsync(client, cancellationToken).ConfigureAwait(false);

        var errors = new List<string>();

        IReadOnlyList<PrReviewNode> reviews = [];
        if (result.TryGet<PrReviewsNode>(ReviewsAlias, out var reviewsNode, out var reviewsError) && reviewsNode?.PullRequest?.Reviews?.Nodes is { } rn)
        {
            reviews = rn;
        }
        else if (reviewsError is not null)
        {
            errors.Add($"{ReviewsAlias}: {reviewsError}");
        }

        IReadOnlyList<PrReviewCommentNode> reviewComments = [];
        if (result.TryGet<PrReviewCommentsNode>(ReviewCommentsAlias, out var commentsNode, out var commentsError) && commentsNode?.PullRequest?.ReviewComments?.Nodes is { } cn)
        {
            reviewComments = cn;
        }
        else if (commentsError is not null)
        {
            errors.Add($"{ReviewCommentsAlias}: {commentsError}");
        }

        IReadOnlyList<ReviewThreadNode> reviewThreads = [];
        if (result.TryGet<RepositoryWithPullRequest>(ReviewThreadsAlias, out var threadsNode, out var threadsError) && threadsNode?.PullRequest?.ReviewThreads?.Nodes is { } tn)
        {
            reviewThreads = tn;
        }
        else if (threadsError is not null)
        {
            errors.Add($"{ReviewThreadsAlias}: {threadsError}");
        }

        return new PrReviewBundle(
            Owner: owner,
            Repo: repo,
            Number: number,
            Reviews: reviews,
            ReviewComments: reviewComments,
            ReviewThreads: reviewThreads,
            Errors: errors);
    }

    private static string BuildReviewsBody(string owner, string repo, int number, int first) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """
            repository(owner: "{0}", name: "{1}") {{
              pullRequest(number: {2}) {{
                reviews(first: {3}) {{
                  nodes {{
                    databaseId
                    state
                    body
                    submittedAt
                    url
                    commit {{ oid }}
                    author {{ login }}
                  }}
                }}
              }}
            }}
            """,
            EscapeGraphQLString(owner),
            EscapeGraphQLString(repo),
            number,
            first);

    private static string BuildReviewCommentsBody(string owner, string repo, int number, int first) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """
            repository(owner: "{0}", name: "{1}") {{
              pullRequest(number: {2}) {{
                reviewThreads(first: {3}) {{
                  nodes {{
                    id
                    isResolved
                    path
                    line
                    comments(first: 100) {{
                      nodes {{
                        databaseId
                        body
                        path
                        position
                        originalPosition
                        diffHunk
                        commit {{ oid }}
                        url
                        createdAt
                        updatedAt
                        author {{ login }}
                      }}
                    }}
                  }}
                }}
              }}
            }}
            """,
            EscapeGraphQLString(owner),
            EscapeGraphQLString(repo),
            number,
            first);

    private static string BuildReviewThreadsBody(string owner, string repo, int number, int first) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """
            repository(owner: "{0}", name: "{1}") {{
              pullRequest(number: {2}) {{
                reviewThreads(first: {3}) {{
                  nodes {{
                    id
                    isResolved
                    isOutdated
                    path
                    line
                    comments(first: 50) {{ nodes {{ id databaseId body author {{ login }} }} }}
                  }}
                }}
              }}
            }}
            """,
            EscapeGraphQLString(owner),
            EscapeGraphQLString(repo),
            number,
            first);

    private static string EscapeGraphQLString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Envelope for the reviews-section sub-query.</summary>
    public sealed record PrReviewsNode(
        [property: JsonPropertyName("pullRequest")] PullRequestWithReviews? PullRequest);

    /// <summary>Pull request containing the reviews connection.</summary>
    public sealed record PullRequestWithReviews(
        [property: JsonPropertyName("reviews")] PrReviewConnection? Reviews);

    /// <summary>Connection envelope for reviews.</summary>
    public sealed record PrReviewConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<PrReviewNode> Nodes);

    /// <summary>A single review on a pull request.</summary>
    public sealed record PrReviewNode(
        [property: JsonPropertyName("databaseId")] long? DatabaseId,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("submittedAt")] DateTimeOffset? SubmittedAt,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("commit")] PrReviewCommit? Commit,
        [property: JsonPropertyName("author")] PrReviewAuthor? Author);

    /// <summary>Commit reference inside a review.</summary>
    public sealed record PrReviewCommit(
        [property: JsonPropertyName("oid")] string? Oid);

    /// <summary>Author reference inside a review.</summary>
    public sealed record PrReviewAuthor(
        [property: JsonPropertyName("login")] string Login);

    /// <summary>Envelope for the review-comments-section sub-query.</summary>
    public sealed record PrReviewCommentsNode(
        [property: JsonPropertyName("pullRequest")] PullRequestWithReviewComments? PullRequest);

    /// <summary>Pull request with the thread-keyed review-comments projection.</summary>
    public sealed record PullRequestWithReviewComments(
        [property: JsonPropertyName("reviewThreads")] PrReviewCommentThreadConnection? ReviewComments);

    /// <summary>
    /// Thread-level projection of review comments. The bundle skill
    /// flattens this into a list of line-level comments, preserving the
    /// thread <c>isResolved</c> flag on each comment so downstream
    /// consumers don't lose the signal.
    /// </summary>
    public sealed record PrReviewCommentThreadConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<PrReviewCommentNode> Nodes);

    /// <summary>A single comment inside a review thread — line-level.</summary>
    public sealed record PrReviewCommentNode(
        [property: JsonPropertyName("id")] string? ThreadId,
        [property: JsonPropertyName("isResolved")] bool IsResolved,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("comments")] PrReviewCommentInnerConnection? Comments);

    /// <summary>Inner connection holding actual comment bodies.</summary>
    public sealed record PrReviewCommentInnerConnection(
        [property: JsonPropertyName("nodes")] IReadOnlyList<PrReviewCommentInner> Nodes);

    /// <summary>A line-level review comment.</summary>
    public sealed record PrReviewCommentInner(
        [property: JsonPropertyName("databaseId")] long? DatabaseId,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("position")] int? Position,
        [property: JsonPropertyName("originalPosition")] int? OriginalPosition,
        [property: JsonPropertyName("diffHunk")] string? DiffHunk,
        [property: JsonPropertyName("commit")] PrReviewCommit? Commit,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("author")] PrReviewAuthor? Author);

    /// <summary>Aggregated bundle result returned by <see cref="ExecuteAsync"/>.</summary>
    public sealed record PrReviewBundle(
        string Owner,
        string Repo,
        int Number,
        IReadOnlyList<PrReviewNode> Reviews,
        IReadOnlyList<PrReviewCommentNode> ReviewComments,
        IReadOnlyList<ReviewThreadNode> ReviewThreads,
        IReadOnlyList<string> Errors);
}