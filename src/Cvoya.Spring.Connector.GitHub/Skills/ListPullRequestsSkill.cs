// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists pull requests in a repository, filtered by state / head / base
/// with optional sort. Accepts small conveniences: <c>headWithOwner</c> (auto-prefixed
/// with <c>owner:</c> when needed) lines up with how callers usually think about
/// "PRs from this branch".
/// </summary>
public class ListPullRequestsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListPullRequestsSkill>();

    /// <summary>
    /// Lists pull requests matching the supplied filters.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="state">State filter: <c>open</c> (default), <c>closed</c>, or <c>all</c>.</param>
    /// <param name="head">Optional head filter in <c>user:branch</c> form. Passed through as-is when provided.</param>
    /// <param name="base">Optional base branch filter.</param>
    /// <param name="sort">Optional sort key: <c>created</c> (default), <c>updated</c>, <c>popularity</c>, <c>long-running</c>.</param>
    /// <param name="direction">Optional sort direction: <c>asc</c> or <c>desc</c> (default).</param>
    /// <param name="maxResults">Maximum number of pull requests to return. Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the matching pull requests.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string? state,
        string? head,
        string? @base,
        string? sort,
        string? direction,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var filter = new PullRequestRequest
        {
            State = ParseState(state),
            SortProperty = ParseSort(sort),
            SortDirection = ParseDirection(direction),
        };

        if (!string.IsNullOrWhiteSpace(head))
        {
            filter.Head = head;
        }

        if (!string.IsNullOrWhiteSpace(@base))
        {
            filter.Base = @base;
        }

        var options = new ApiOptions
        {
            PageSize = Math.Clamp(maxResults, 1, 100),
            PageCount = 1,
        };

        _logger.LogInformation(
            "Listing PRs in {Owner}/{Repo} state={State} head={Head} base={Base} sort={Sort}/{Dir} max={Max}",
            owner, repo, state ?? "open", head ?? "*", @base ?? "*", sort ?? "created", direction ?? "desc", options.PageSize);

        var prs = await gitHubClient.PullRequest.GetAllForRepository(owner, repo, filter, options);

        var projected = prs.Select(PullRequestProjection.ProjectSummary).ToArray();
        return JsonSerializer.SerializeToElement(new { pull_requests = projected, count = projected.Length });
    }

    internal static ItemStateFilter ParseState(string? state) =>
        (state?.ToLowerInvariant()) switch
        {
            "closed" => ItemStateFilter.Closed,
            "all" => ItemStateFilter.All,
            _ => ItemStateFilter.Open,
        };

    internal static PullRequestSort ParseSort(string? sort) =>
        (sort?.ToLowerInvariant()) switch
        {
            "updated" => PullRequestSort.Updated,
            "popularity" => PullRequestSort.Popularity,
            "long-running" or "longrunning" or "long_running" => PullRequestSort.LongRunning,
            _ => PullRequestSort.Created,
        };

    internal static SortDirection ParseDirection(string? direction) =>
        (direction?.ToLowerInvariant()) switch
        {
            "asc" or "ascending" => SortDirection.Ascending,
            _ => SortDirection.Descending,
        };
}
