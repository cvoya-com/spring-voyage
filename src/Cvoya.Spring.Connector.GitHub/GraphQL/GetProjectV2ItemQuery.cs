// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL query for a single project item by GraphQL node id. Resolves
/// the item via the top-level <c>node(id)</c> field and returns the same
/// content + field-values projection as the list query, so skill output
/// matches regardless of which query produced the record.
/// </summary>
public static class GetProjectV2ItemQuery
{
    /// <summary>The GraphQL query text. Parameterized on $id.</summary>
    public const string Query = """
        query GetProjectV2Item($id: ID!, $firstValues: Int = 50) {
          node(id: $id) {
            __typename
            ... on ProjectV2Item {
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
        """;

    /// <summary>Builds the variables dictionary.</summary>
    public static Dictionary<string, object?> Variables(string itemId, int firstValues = 50) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = itemId,
            ["firstValues"] = Math.Clamp(firstValues, 1, 100),
        };
}