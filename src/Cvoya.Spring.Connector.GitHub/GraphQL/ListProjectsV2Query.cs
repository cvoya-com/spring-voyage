// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL query for listing Projects v2 owned by a user or organization.
/// The <c>repositoryOwner</c> interface covers both <c>User</c> and
/// <c>Organization</c> in a single call, so we don't need to discriminate
/// between the two at the caller site.
/// </summary>
public static class ListProjectsV2Query
{
    /// <summary>The GraphQL query text. Parameterized on $owner, $first.</summary>
    public const string Query = """
        query ListProjectsV2($owner: String!, $first: Int = 30) {
          repositoryOwner(login: $owner) {
            login
            ... on ProjectV2Owner {
              projectsV2(first: $first) {
                nodes {
                  id
                  number
                  title
                  url
                  closed
                  public
                  shortDescription
                  createdAt
                  updatedAt
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Builds the variables dictionary. <paramref name="first"/> is capped at
    /// 100 per GitHub's connection limits.
    /// </summary>
    public static Dictionary<string, object?> Variables(string owner, int first = 30) =>
        new(StringComparer.Ordinal)
        {
            ["owner"] = owner,
            ["first"] = Math.Clamp(first, 1, 100),
        };
}