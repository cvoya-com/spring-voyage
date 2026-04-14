// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// Centralizes the GraphQL query text for fetching review threads on a single
/// pull request. Review-thread resolution state is a GraphQL-only capability
/// (the REST API exposes review comments but not thread-level resolution),
/// which is why this is the canonical first use case for v2's GraphQL path.
/// </summary>
public static class GetPullRequestReviewThreadsQuery
{
    /// <summary>The GraphQL query text. Parameterized on $owner, $repo, $number.</summary>
    public const string Query = """
        query PullRequestReviewThreads($owner: String!, $repo: String!, $number: Int!, $first: Int = 100, $firstComments: Int = 50) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              reviewThreads(first: $first) {
                nodes {
                  id
                  isResolved
                  isOutdated
                  path
                  line
                  comments(first: $firstComments) {
                    nodes {
                      id
                      databaseId
                      body
                      author { login }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Builds the variables dictionary for a single-PR review-thread query.
    /// </summary>
    public static Dictionary<string, object?> Variables(string owner, string repo, int number, int first = 100, int firstComments = 50) =>
        new(StringComparer.Ordinal)
        {
            ["owner"] = owner,
            ["repo"] = repo,
            ["number"] = number,
            ["first"] = first,
            ["firstComments"] = firstComments,
        };
}