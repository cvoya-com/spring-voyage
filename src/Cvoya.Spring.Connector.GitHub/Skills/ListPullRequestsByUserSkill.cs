// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists pull requests filtered by either author or assignee. GitHub's per-repo
/// PR list endpoint doesn't support these filters, so this skill routes through
/// the Search API (<c>q=repo:owner/name is:pr author:login</c> or <c>assignee:login</c>).
/// One skill class backs both <c>github_list_pull_requests_by_author</c> and
/// <c>github_list_pull_requests_by_assignee</c>; the <c>role</c> argument selects
/// which qualifier is emitted.
/// </summary>
public class ListPullRequestsByUserSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListPullRequestsByUserSkill>();

    /// <summary>
    /// Role selector for the search qualifier.
    /// </summary>
    public enum UserRole
    {
        /// <summary>Filter by the user who opened the pull request.</summary>
        Author,
        /// <summary>Filter by a user assigned to the pull request.</summary>
        Assignee,
    }

    /// <summary>
    /// Lists PRs authored by or assigned to the given user in the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="login">The GitHub login to filter by.</param>
    /// <param name="role">Whether <paramref name="login"/> is the author or the assignee.</param>
    /// <param name="state">State filter: <c>open</c> (default), <c>closed</c>, or <c>all</c>.</param>
    /// <param name="maxResults">Maximum number of pull requests to return. Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the matching pull requests.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string login,
        UserRole role,
        string? state,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var perPage = Math.Clamp(maxResults, 1, 100);
        var request = new SearchIssuesRequest
        {
            Is = new[] { IssueIsQualifier.PullRequest },
            Repos = new RepositoryCollection { { owner, repo } },
            PerPage = perPage,
            Page = 1,
            SortField = IssueSearchSort.Updated,
            Order = SortDirection.Descending,
        };

        switch (role)
        {
            case UserRole.Author:
                request.Author = login;
                break;
            case UserRole.Assignee:
                request.Assignee = login;
                break;
        }

        var itemState = ListPullRequestsSkill.ParseState(state);
        if (itemState != ItemStateFilter.All)
        {
            request.State = itemState == ItemStateFilter.Closed ? ItemState.Closed : ItemState.Open;
        }

        _logger.LogInformation(
            "Searching PRs in {Owner}/{Repo} by {Role}={Login} state={State} max={Max}",
            owner, repo, role, login, state ?? "open", perPage);

        var result = await gitHubClient.Search.SearchIssues(request);

        // Search results surface as Issue records; they carry enough for a summary shape.
        var projected = result.Items.Select(i => new
        {
            number = i.Number,
            title = i.Title,
            state = i.State.StringValue,
            html_url = i.HtmlUrl,
            author = i.User?.Login,
            assignees = i.Assignees?.Select(a => a.Login).ToArray() ?? [],
            labels = i.Labels?.Select(l => l.Name).ToArray() ?? [],
            created_at = i.CreatedAt,
            updated_at = i.UpdatedAt,
            closed_at = i.ClosedAt,
        }).ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            pull_requests = projected,
            count = projected.Length,
            total_count = result.TotalCount,
            incomplete_results = result.IncompleteResults,
        });
    }
}
