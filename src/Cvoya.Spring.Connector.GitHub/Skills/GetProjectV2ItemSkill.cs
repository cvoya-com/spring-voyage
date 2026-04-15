// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches a single Projects v2 item by GraphQL node id, returning the
/// same content + field-values projection as the list query so callers
/// can treat items uniformly regardless of which query produced them.
/// </summary>
public class GetProjectV2ItemSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetProjectV2ItemSkill>();

    /// <summary>Fetches a single item by id.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching Projects v2 item {ItemId}", itemId);

        var response = await graphQLClient.QueryAsync<GetProjectV2ItemResponse>(
            GetProjectV2ItemQuery.Query,
            GetProjectV2ItemQuery.Variables(itemId),
            cancellationToken);

        if (response.Node is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                item_id = itemId,
                found = false,
            });
        }

        var item = ProjectV2Projection.ProjectItem(response.Node);

        return JsonSerializer.SerializeToElement(new
        {
            found = true,
            item,
        });
    }
}