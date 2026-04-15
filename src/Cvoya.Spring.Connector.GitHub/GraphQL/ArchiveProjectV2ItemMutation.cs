// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL mutation that soft-archives a Projects v2 item. The item stays
/// queryable (with <c>isArchived = true</c>) — use
/// <see cref="DeleteProjectV2ItemMutation"/> for a hard delete.
/// </summary>
public static class ArchiveProjectV2ItemMutation
{
    /// <summary>The GraphQL mutation text. Parameterized on $projectId, $itemId.</summary>
    public const string Mutation = """
        mutation ArchiveProjectV2Item($projectId: ID!, $itemId: ID!) {
          archiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
            item {
              id
              type
              isArchived
              createdAt
              updatedAt
            }
          }
        }
        """;

    /// <summary>Builds the variables dictionary.</summary>
    public static Dictionary<string, object?> Variables(string projectId, string itemId) =>
        new(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["itemId"] = itemId,
        };
}