// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Attaches an existing Issue or PullRequest to a Projects v2 board via the
/// <c>addProjectV2ItemById</c> GraphQL mutation.
/// </summary>
/// <remarks>
/// On success the skill invalidates the board-level cache tag so a
/// subsequent <c>github_list_project_v2_items</c> re-queries GitHub rather
/// than serving a stale list. The <paramref name="owner"/> /
/// <paramref name="number"/> arguments are optional — callers that know the
/// board slug should supply them so the tag flushes precisely; when absent
/// the skill still succeeds but the caller must live with a stale read
/// until the TTL expires (or a webhook invalidates the tag).
/// </remarks>
public class AddProjectV2ItemSkill(
    IGitHubGraphQLClient graphQLClient,
    IGitHubResponseCache responseCache,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AddProjectV2ItemSkill>();

    /// <summary>Attaches <paramref name="contentId"/> to <paramref name="projectId"/>.</summary>
    /// <param name="projectId">The GraphQL node id of the Projects v2 board.</param>
    /// <param name="contentId">The GraphQL node id of the Issue or PullRequest to attach.</param>
    /// <param name="owner">Optional owner login for cache-tag invalidation.</param>
    /// <param name="number">Optional project number for cache-tag invalidation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> ExecuteAsync(
        string projectId,
        string contentId,
        string? owner = null,
        int? number = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Adding content {ContentId} to Projects v2 board {ProjectId}", contentId, projectId);

        var response = await graphQLClient.MutateAsync<AddProjectV2ItemResponse>(
            AddProjectV2ItemMutation.Mutation,
            AddProjectV2ItemMutation.Variables(projectId, contentId),
            cancellationToken);

        var item = response.AddProjectV2ItemById?.Item;
        if (item is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                project_id = projectId,
                content_id = contentId,
                added = false,
            });
        }

        // Board-level list is now stale; flush it. The per-item cache is
        // unaffected — a brand-new item can't have a prior cache entry.
        if (owner is not null && number is not null)
        {
            await responseCache.InvalidateByTagAsync(
                CacheTags.ProjectV2(owner, number.Value),
                cancellationToken);
        }

        return JsonSerializer.SerializeToElement(new
        {
            project_id = projectId,
            content_id = contentId,
            added = true,
            item = ProjectV2Projection.ProjectItem(item),
        });
    }
}