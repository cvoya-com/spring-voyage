// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches review threads for a pull request via GraphQL, returning their
/// resolution state alongside path/line metadata and per-comment bodies.
/// Required because the REST API does not expose thread-level resolution
/// (only review comments).
/// </summary>
public class ListReviewThreadsSkill(IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListReviewThreadsSkill>();

    /// <summary>
    /// Lists review threads for the given pull request.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Listing review threads for {Owner}/{Repo}#{Number}", owner, repo, number);

        var response = await graphQLClient.QueryAsync<ReviewThreadsResponse>(
            GetPullRequestReviewThreadsQuery.Query,
            GetPullRequestReviewThreadsQuery.Variables(owner, repo, number),
            cancellationToken);

        var threads = response.Repository?.PullRequest?.ReviewThreads?.Nodes ?? [];

        var projected = threads
            .Select(t => new
            {
                thread_id = t.Id,
                is_resolved = t.IsResolved,
                is_outdated = t.IsOutdated,
                path = t.Path,
                line = t.Line,
                comments = (t.Comments?.Nodes ?? [])
                    .Select(c => new
                    {
                        id = c.Id,
                        database_id = c.DatabaseId,
                        body = c.Body,
                        author = c.Author?.Login,
                    })
                    .ToArray(),
            })
            .ToArray();

        var unresolvedCount = threads.Count(t => !t.IsResolved);

        return JsonSerializer.SerializeToElement(new
        {
            owner,
            repo,
            number,
            thread_count = threads.Count,
            unresolved_count = unresolvedCount,
            has_unresolved_review_threads = unresolvedCount > 0,
            threads = projected,
        });
    }
}