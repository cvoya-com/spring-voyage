// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Searches issues / pull requests / comments for <c>@user</c> mentions of the
/// supplied GitHub login inside a given repository. Used by planners to
/// reconstruct inbound work for an agent — it is the building block
/// <see cref="GetPriorWorkContextSkill"/> composes on.
/// </summary>
public class SearchMentionsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SearchMentionsSkill>();

    /// <summary>
    /// Executes the mention search against <c>GET /search/issues</c>. Uses
    /// GitHub's <c>mentions:</c> qualifier which covers issues, pull requests,
    /// and their top-level comments in a single query.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="user">The GitHub login (without the leading @).</param>
    /// <param name="since">Optional lower bound — only include items updated after this timestamp.</param>
    /// <param name="maxResults">Maximum number of mentions to return. Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string user,
        DateTimeOffset? since,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        var bareLogin = user.StartsWith('@') ? user[1..] : user;

        var request = new SearchIssuesRequest($"mentions:{bareLogin}")
        {
            Repos = new RepositoryCollection { { owner, repo } },
            PerPage = Math.Clamp(maxResults, 1, 100),
            Page = 1,
        };

        if (since is { } sinceValue)
        {
            request.Updated = new DateRange(sinceValue, SearchQualifierOperator.GreaterThan);
        }

        _logger.LogInformation(
            "Searching mentions of @{User} in {Owner}/{Repo} since={Since} max={Max}",
            bareLogin, owner, repo, since?.ToString("o") ?? "any", request.PerPage);

        var results = await gitHubClient.Search.SearchIssues(request);

        var mentions = results.Items
            .Select(i => new
            {
                url = i.HtmlUrl,
                type = i.PullRequest != null ? "pull_request" : "issue",
                number = i.Number,
                title = i.Title,
                excerpt = BuildExcerpt(i.Body, bareLogin),
                state = i.State.StringValue,
                author = i.User?.Login,
                created_at = i.CreatedAt,
                updated_at = i.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            mentions,
            count = mentions.Length,
            total_count = results.TotalCount,
            incomplete = results.IncompleteResults,
        });
    }

    /// <summary>
    /// Extracts a short excerpt around the first occurrence of <c>@user</c> in
    /// the body so callers can cheaply preview the mention context without
    /// fetching the full item.
    /// </summary>
    private static string? BuildExcerpt(string? body, string user)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var needle = "@" + user;
        var idx = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Mention may be in a comment the search API matched on rather than
            // the body itself — fall back to a leading slice.
            return body.Length <= 240 ? body : body[..240];
        }

        var start = Math.Max(0, idx - 80);
        var end = Math.Min(body.Length, idx + needle.Length + 160);
        return body[start..end];
    }
}