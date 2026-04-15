// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL mutation that attaches an existing Issue or PullRequest node to
/// a Projects v2 board. The <c>contentId</c> is the GraphQL node id of the
/// content to attach — draft issues use the separate
/// <c>addProjectV2DraftIssue</c> mutation (out of scope here; see #284).
/// </summary>
public static class AddProjectV2ItemMutation
{
    /// <summary>The GraphQL mutation text. Parameterized on $projectId, $contentId.</summary>
    public const string Mutation = """
        mutation AddProjectV2Item($projectId: ID!, $contentId: ID!) {
          addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
            item {
              id
              type
              isArchived
              createdAt
              updatedAt
              content {
                __typename
                ... on Issue {
                  id
                  number
                  title
                  url
                  state
                  repository { nameWithOwner }
                }
                ... on PullRequest {
                  id
                  number
                  title
                  url
                  state
                  repository { nameWithOwner }
                }
                ... on DraftIssue {
                  id
                  title
                  body
                }
              }
            }
          }
        }
        """;

    /// <summary>Builds the variables dictionary.</summary>
    public static Dictionary<string, object?> Variables(string projectId, string contentId) =>
        new(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["contentId"] = contentId,
        };
}