// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Lists items on a Projects v2 board — a paged slice of issues / PRs /
/// draft issues together with their field values. Callers pass an opaque
/// <c>cursor</c> from a previous response's <c>end_cursor</c> to advance.
/// </summary>
public class ListProjectV2ItemsSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListProjectV2ItemsSkill>();

    /// <summary>Lists items in <paramref name="owner"/>'s project <paramref name="number"/>.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        int number,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Listing Projects v2 items for {Owner}/#{Number} (cursor={HasCursor}, limit={Limit})",
            owner, number, cursor is not null, limit);

        var response = await graphQLClient.QueryAsync<ListProjectV2ItemsResponse>(
            ListProjectV2ItemsQuery.Query,
            ListProjectV2ItemsQuery.Variables(owner, number, limit, cursor),
            cancellationToken);

        var project = response.RepositoryOwner?.ProjectV2;
        if (project is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                owner,
                number,
                found = false,
                item_count = 0,
                has_next_page = false,
                end_cursor = (string?)null,
                items = Array.Empty<object>(),
            });
        }

        var items = (project.Items?.Nodes ?? [])
            .Select(ProjectV2Projection.ProjectItem)
            .ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            owner,
            number,
            found = true,
            project_id = project.Id,
            project_title = project.Title,
            item_count = items.Length,
            has_next_page = project.Items?.PageInfo?.HasNextPage ?? false,
            end_cursor = project.Items?.PageInfo?.EndCursor,
            items,
        });
    }
}