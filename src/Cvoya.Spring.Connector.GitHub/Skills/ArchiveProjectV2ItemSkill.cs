// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Soft-archives a Projects v2 item via the <c>archiveProjectV2Item</c>
/// GraphQL mutation. The item remains queryable with
/// <c>is_archived = true</c>; use <see cref="DeleteProjectV2ItemSkill"/>
/// for a hard delete.
/// </summary>
public class ArchiveProjectV2ItemSkill(
    IGitHubGraphQLClient graphQLClient,
    IGitHubResponseCache responseCache,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ArchiveProjectV2ItemSkill>();

    /// <summary>Archives <paramref name="itemId"/> on <paramref name="projectId"/>.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string projectId,
        string itemId,
        string? owner = null,
        int? number = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Archiving Projects v2 item {ItemId} on board {ProjectId}", itemId, projectId);

        var response = await graphQLClient.MutateAsync<ArchiveProjectV2ItemResponse>(
            ArchiveProjectV2ItemMutation.Mutation,
            ArchiveProjectV2ItemMutation.Variables(projectId, itemId),
            cancellationToken);

        var archived = response.ArchiveProjectV2Item?.Item;

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
            archived = archived is not null,
            is_archived = archived?.IsArchived ?? true,
            updated_at = archived?.UpdatedAt,
        });
    }
}