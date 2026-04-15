// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL query for a single Projects v2 board including its field
/// definitions. Field definitions are a discriminated union (plain fields,
/// single-select fields, iteration fields) that we flatten to a single
/// record via <c>__typename</c>-conditioned inline fragments.
/// </summary>
public static class GetProjectV2Query
{
    /// <summary>
    /// GraphQL query text. Parameterized on $owner, $number, $firstFields.
    /// The inline fragments return a superset of the fields we care about
    /// — unused variants serialize as nulls on the flattened DTO.
    /// </summary>
    public const string Query = """
        query GetProjectV2($owner: String!, $number: Int!, $firstFields: Int = 50) {
          repositoryOwner(login: $owner) {
            login
            ... on ProjectV2Owner {
              projectV2(number: $number) {
                id
                number
                title
                url
                closed
                public
                shortDescription
                readme
                createdAt
                updatedAt
                fields(first: $firstFields) {
                  nodes {
                    __typename
                    ... on ProjectV2FieldCommon {
                      id
                      name
                      dataType
                    }
                    ... on ProjectV2SingleSelectField {
                      options {
                        id
                        name
                      }
                    }
                    ... on ProjectV2IterationField {
                      configuration {
                        duration
                        startDay
                        iterations {
                          id
                          title
                          startDate
                          duration
                        }
                        completedIterations {
                          id
                          title
                          startDate
                          duration
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

    /// <summary>Builds the variables dictionary.</summary>
    public static Dictionary<string, object?> Variables(string owner, int number, int firstFields = 50) =>
        new(StringComparer.Ordinal)
        {
            ["owner"] = owner,
            ["number"] = number,
            ["firstFields"] = Math.Clamp(firstFields, 1, 100),
        };
}