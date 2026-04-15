// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Lists Projects v2 boards owned by a user or organization. Projects v2
/// has no REST surface — every read and write runs through GraphQL against
/// the <c>repositoryOwner</c> interface, which resolves to either a
/// <c>User</c> or an <c>Organization</c>.
/// </summary>
public class ListProjectsV2Skill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListProjectsV2Skill>();

    /// <summary>Lists up to <paramref name="first"/> projects owned by <paramref name="owner"/>.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        int first = 30,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing Projects v2 for owner {Owner} (first={First})", owner, first);

        var response = await graphQLClient.QueryAsync<ListProjectsV2Response>(
            ListProjectsV2Query.Query,
            ListProjectsV2Query.Variables(owner, first),
            cancellationToken);

        var nodes = response.RepositoryOwner?.ProjectsV2?.Nodes ?? [];

        var projects = nodes
            .Select(p => new
            {
                id = p.Id,
                number = p.Number,
                title = p.Title,
                url = p.Url,
                closed = p.Closed,
                @public = p.Public,
                short_description = p.ShortDescription,
                created_at = p.CreatedAt,
                updated_at = p.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            owner,
            owner_exists = response.RepositoryOwner is not null,
            project_count = projects.Length,
            projects,
        });
    }
}