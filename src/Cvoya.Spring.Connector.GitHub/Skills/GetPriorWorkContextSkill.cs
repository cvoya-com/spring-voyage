// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Produces a structured "prior work" summary for an agent login in a given
/// repository — mentions directed at the agent, PRs it has authored, and
/// issues it has commented on / is assigned to. Composed on top of
/// <see cref="SearchMentionsSkill"/> plus a handful of targeted searches so
/// the planner can build long-running context without enumerating the repo.
/// </summary>
public class GetPriorWorkContextSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetPriorWorkContextSkill>();

    /// <summary>
    /// Executes the prior-work summary. Each bucket is independently page-capped
    /// so a noisy bucket cannot starve the others.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="user">The GitHub login whose prior work we are summarizing.</param>
    /// <param name="since">Optional lower bound — only include items updated after this timestamp.</param>
    /// <param name="maxPerBucket">Maximum items per bucket (mentions / authored PRs / commented / assigned). Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string user,
        DateTimeOffset? since,
        int maxPerBucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        var bareLogin = user.StartsWith('@') ? user[1..] : user;
        var limit = Math.Clamp(maxPerBucket, 1, 100);

        _logger.LogInformation(
            "Gathering prior-work context for @{User} in {Owner}/{Repo} (limit {Limit} per bucket)",
            bareLogin, owner, repo, limit);

        var mentions = await QueryAsync($"mentions:{bareLogin}", owner, repo, since, limit);
        var authoredPulls = await QueryAsync($"author:{bareLogin} is:pr", owner, repo, since, limit);
        var commented = await QueryAsync($"commenter:{bareLogin} is:issue", owner, repo, since, limit);
        var assigned = await QueryAsync($"assignee:{bareLogin} is:issue", owner, repo, since, limit);

        var summary = new
        {
            user = bareLogin,
            repository = new { owner, repo, full_name = $"{owner}/{repo}" },
            since,
            mentions = new
            {
                count = mentions.Length,
                items = mentions,
            },
            authored_pull_requests = new
            {
                count = authoredPulls.Length,
                items = authoredPulls,
            },
            commented_issues = new
            {
                count = commented.Length,
                items = commented,
            },
            assigned_issues = new
            {
                count = assigned.Length,
                items = assigned,
            },
        };

        return JsonSerializer.SerializeToElement(summary);
    }

    private async Task<object[]> QueryAsync(
        string qualifier,
        string owner,
        string repo,
        DateTimeOffset? since,
        int perBucket)
    {
        var request = new SearchIssuesRequest(qualifier)
        {
            Repos = new RepositoryCollection { { owner, repo } },
            PerPage = perBucket,
            Page = 1,
        };

        if (since is { } sinceValue)
        {
            request.Updated = new DateRange(sinceValue, SearchQualifierOperator.GreaterThan);
        }

        var results = await gitHubClient.Search.SearchIssues(request);
        return results.Items
            .Select(i => (object)new
            {
                url = i.HtmlUrl,
                type = i.PullRequest != null ? "pull_request" : "issue",
                number = i.Number,
                title = i.Title,
                state = i.State.StringValue,
                author = i.User?.Login,
                created_at = i.CreatedAt,
                updated_at = i.UpdatedAt,
            })
            .ToArray();
    }
}