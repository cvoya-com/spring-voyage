// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists issues in a GitHub repository, filtered by state / labels / assignee.
/// </summary>
public class ListIssuesSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListIssuesSkill>();

    /// <summary>
    /// Lists issues matching the given filters.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="state">
    /// Filter by state: <c>open</c> (default), <c>closed</c>, or <c>all</c>.
    /// </param>
    /// <param name="labels">Optional label names to filter by (logical AND).</param>
    /// <param name="assignee">Optional assignee login, or <c>*</c> for any assignee, or <c>none</c> for unassigned.</param>
    /// <param name="maxResults">Maximum number of issues to return. Caps at 100 to match GitHub pagination.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the matching issues.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string? state,
        string[] labels,
        string? assignee,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var filter = new RepositoryIssueRequest
        {
            State = ParseState(state),
        };

        foreach (var label in labels)
        {
            filter.Labels.Add(label);
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            filter.Assignee = assignee;
        }

        var options = new ApiOptions
        {
            PageSize = Math.Clamp(maxResults, 1, 100),
            PageCount = 1,
        };

        _logger.LogInformation(
            "Listing issues in {Owner}/{Repo} state={State} labels={Labels} assignee={Assignee} max={Max}",
            owner, repo, state ?? "open", string.Join(",", labels), assignee ?? "any", options.PageSize);

        var issues = await gitHubClient.Issue.GetAllForRepository(owner, repo, filter, options);

        // Intentionally filter out pull requests — GitHub's issues API returns both,
        // but every v1 caller of "list issues" wanted issues only.
        var projected = issues
            .Where(i => i.PullRequest == null)
            .Select(i => new
            {
                number = i.Number,
                title = i.Title,
                state = i.State.StringValue,
                labels = i.Labels.Select(l => l.Name).ToArray(),
                assignees = i.Assignees.Select(a => a.Login).ToArray(),
                author = i.User?.Login,
                html_url = i.HtmlUrl,
                created_at = i.CreatedAt,
                updated_at = i.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.SerializeToElement(new { issues = projected, count = projected.Length });
    }

    private static ItemStateFilter ParseState(string? state) =>
        (state?.ToLowerInvariant()) switch
        {
            "closed" => ItemStateFilter.Closed,
            "all" => ItemStateFilter.All,
            _ => ItemStateFilter.Open,
        };
}