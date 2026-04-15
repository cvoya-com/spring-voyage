// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL query for a paged slice of items in a Projects v2 board,
/// including each item's content (Issue / PullRequest / DraftIssue) and
/// its field values. Field values are returned as a polymorphic list —
/// we let callers project the typename-tagged shape directly because a
/// strongly-typed union for five value types would outweigh the handful
/// of projection sites.
/// </summary>
public static class ListProjectV2ItemsQuery
{
    /// <summary>The GraphQL query text.</summary>
    public const string Query = """
        query ListProjectV2Items($owner: String!, $number: Int!, $first: Int = 50, $after: String, $firstValues: Int = 20) {
          repositoryOwner(login: $owner) {
            ... on ProjectV2Owner {
              projectV2(number: $number) {
                id
                number
                title
                items(first: $first, after: $after) {
                  pageInfo { endCursor hasNextPage }
                  nodes {
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
                    fieldValues(first: $firstValues) {
                      nodes {
                        __typename
                        ... on ProjectV2ItemFieldTextValue {
                          text
                          field { ... on ProjectV2FieldCommon { id name dataType } }
                        }
                        ... on ProjectV2ItemFieldNumberValue {
                          number
                          field { ... on ProjectV2FieldCommon { id name dataType } }
                        }
                        ... on ProjectV2ItemFieldDateValue {
                          date
                          field { ... on ProjectV2FieldCommon { id name dataType } }
                        }
                        ... on ProjectV2ItemFieldSingleSelectValue {
                          optionId
                          name
                          field { ... on ProjectV2FieldCommon { id name dataType } }
                        }
                        ... on ProjectV2ItemFieldIterationValue {
                          iterationId
                          title
                          startDate
                          duration
                          field { ... on ProjectV2FieldCommon { id name dataType } }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Builds the variables dictionary with optional cursor.</summary>
    public static Dictionary<string, object?> Variables(string owner, int number, int first = 50, string? after = null, int firstValues = 20) =>
        new(StringComparer.Ordinal)
        {
            ["owner"] = owner,
            ["number"] = number,
            ["first"] = Math.Clamp(first, 1, 100),
            ["after"] = after,
            ["firstValues"] = Math.Clamp(firstValues, 1, 50),
        };
}