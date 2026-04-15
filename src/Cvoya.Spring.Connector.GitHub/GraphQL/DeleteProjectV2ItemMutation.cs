// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

/// <summary>
/// GraphQL mutation that hard-deletes a Projects v2 item (as distinct from
/// <see cref="ArchiveProjectV2ItemMutation"/>, which only soft-archives).
/// The payload carries only the <c>deletedItemId</c> since the item no
/// longer exists to serialize.
/// </summary>
public static class DeleteProjectV2ItemMutation
{
    /// <summary>The GraphQL mutation text. Parameterized on $projectId, $itemId.</summary>
    public const string Mutation = """
        mutation DeleteProjectV2Item($projectId: ID!, $itemId: ID!) {
          deleteProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
            deletedItemId
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