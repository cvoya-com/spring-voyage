// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches a single Projects v2 board by owner + number, including its
/// field definitions (the per-project schema clients need in order to read
/// and later mutate field values). Field definitions cover the five main
/// Projects v2 field types: text, number, date, single-select, iteration.
/// </summary>
public class GetProjectV2Skill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetProjectV2Skill>();

    /// <summary>Fetches a project and its field definitions.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching Projects v2 for {Owner}/#{Number}", owner, number);

        var response = await graphQLClient.QueryAsync<GetProjectV2Response>(
            GetProjectV2Query.Query,
            GetProjectV2Query.Variables(owner, number),
            cancellationToken);

        var project = response.RepositoryOwner?.ProjectV2;
        if (project is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                owner,
                number,
                found = false,
            });
        }

        var fields = (project.Fields?.Nodes ?? [])
            .Select(f => new
            {
                id = f.Id,
                name = f.Name,
                data_type = f.DataType,
                options = f.Options?
                    .Select(o => new { id = o.Id, name = o.Name })
                    .ToArray(),
                iteration_configuration = f.Configuration is null ? null : new
                {
                    duration = f.Configuration.Duration,
                    start_day = f.Configuration.StartDay,
                    iterations = (f.Configuration.Iterations ?? [])
                        .Select(i => new { id = i.Id, title = i.Title, start_date = i.StartDate, duration = i.Duration })
                        .ToArray(),
                    completed_iterations = (f.Configuration.CompletedIterations ?? [])
                        .Select(i => new { id = i.Id, title = i.Title, start_date = i.StartDate, duration = i.Duration })
                        .ToArray(),
                },
            })
            .ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            owner,
            number,
            found = true,
            project = new
            {
                id = project.Id,
                number = project.Number,
                title = project.Title,
                url = project.Url,
                closed = project.Closed,
                @public = project.Public,
                short_description = project.ShortDescription,
                readme = project.Readme,
                created_at = project.CreatedAt,
                updated_at = project.UpdatedAt,
            },
            field_count = fields.Length,
            fields,
        });
    }
}