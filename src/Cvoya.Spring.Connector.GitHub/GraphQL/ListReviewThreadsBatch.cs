// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Globalization;

/// <summary>
/// Worked example of <see cref="GraphQLBatch"/>: fetches review threads
/// across many pull requests in a single GraphQL call. Kept small on
/// purpose — the full REST→GraphQL batching migration of bulk skills is a
/// follow-up, not part of this change.
/// </summary>
/// <remarks>
/// Each PR becomes an aliased <c>repository(...) { pullRequest(...) { reviewThreads ... } }</c>
/// sub-query. The returned map is keyed by <c>(owner, repo, number)</c> so
/// callers don't have to reason about the alias space.
/// </remarks>
public static class ListReviewThreadsBatch
{
    /// <summary>One PR coordinate for the batch.</summary>
    public sealed record PullRequestRef(string Owner, string Repo, int Number);

    /// <summary>
    /// Fetches review threads for each <paramref name="pullRequests"/> entry
    /// in a single GraphQL call. Throws if the input exceeds
    /// <see cref="GraphQLBatch.DefaultMaxAliases"/>; callers that need more
    /// should chunk externally.
    /// </summary>
    public static async Task<IReadOnlyDictionary<PullRequestRef, BatchedReviewThreads>> ExecuteAsync(
        IGitHubGraphQLClient client,
        IReadOnlyList<PullRequestRef> pullRequests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pullRequests);

        var batch = new GraphQLBatch();
        var aliasByPr = new Dictionary<string, PullRequestRef>(StringComparer.Ordinal);

        for (var i = 0; i < pullRequests.Count; i++)
        {
            var pr = pullRequests[i];
            var alias = string.Format(CultureInfo.InvariantCulture, "pr_{0}", i);
            aliasByPr[alias] = pr;

            var subQuery = string.Format(
                CultureInfo.InvariantCulture,
                """
                repository(owner: "{0}", name: "{1}") {{
                  pullRequest(number: {2}) {{
                    reviewThreads(first: 100) {{
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
                EscapeGraphQLString(pr.Owner),
                EscapeGraphQLString(pr.Repo),
                pr.Number);

            batch.Add<RepositoryWithPullRequest>(alias, subQuery);
        }

        var result = await batch.ExecuteAsync(client, cancellationToken);

        var output = new Dictionary<PullRequestRef, BatchedReviewThreads>();
        foreach (var (alias, pr) in aliasByPr)
        {
            if (!result.TryGet<RepositoryWithPullRequest>(alias, out var repo, out var error))
            {
                output[pr] = new BatchedReviewThreads(pr, Threads: [], Error: error);
                continue;
            }

            var threads = repo?.PullRequest?.ReviewThreads?.Nodes ?? [];
            output[pr] = new BatchedReviewThreads(pr, Threads: threads, Error: null);
        }
        return output;
    }

    /// <summary>
    /// Per-PR batching result: either the threads or a per-alias error
    /// string (partial failures don't poison the whole call).
    /// </summary>
    public sealed record BatchedReviewThreads(
        PullRequestRef PullRequest,
        IReadOnlyList<ReviewThreadNode> Threads,
        string? Error);

    private static string EscapeGraphQLString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);
}