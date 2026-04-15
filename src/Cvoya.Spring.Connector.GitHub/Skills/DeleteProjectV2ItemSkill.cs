// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Hard-deletes a Projects v2 item via the <c>deleteProjectV2Item</c>
/// GraphQL mutation. The payload is a single <c>deleted_id</c>; there is
/// no remaining item to project since the record is gone.
/// </summary>
/// <remarks>
/// This is distinct from <see cref="ArchiveProjectV2ItemSkill"/> — archive
/// is recoverable, delete is not. Exposed alongside archive so agents can
/// pick the right semantics; most workflows should prefer archive.
/// </remarks>
public class DeleteProjectV2ItemSkill(
    IGitHubGraphQLClient graphQLClient,
    IGitHubResponseCache responseCache,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DeleteProjectV2ItemSkill>();

    /// <summary>Deletes <paramref name="itemId"/> from <paramref name="projectId"/>.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string projectId,
        string itemId,
        string? owner = null,
        int? number = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Deleting Projects v2 item {ItemId} on board {ProjectId}", itemId, projectId);

        var response = await graphQLClient.MutateAsync<DeleteProjectV2ItemResponse>(
            DeleteProjectV2ItemMutation.Mutation,
            DeleteProjectV2ItemMutation.Variables(projectId, itemId),
            cancellationToken);

        var deletedId = response.DeleteProjectV2Item?.DeletedItemId;

        await responseCache.InvalidateByTagAsync(
            CacheTags.ProjectV2Item(itemId),
            cancellationToken);
        if (owner is not null && number is not null)
        {
            await responseCache.InvalidateByTagAsync(
                CacheTags.ProjectV2(owner, number.Value),
                cancellationToken);
        }

        return JsonSerializer.SerializeToElement(new
        {
            project_id = projectId,
            item_id = itemId,
            deleted = deletedId is not null,
            deleted_id = deletedId,
        });
    }
}