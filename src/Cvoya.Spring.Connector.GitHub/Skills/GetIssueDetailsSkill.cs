// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Retrieves detailed information about a GitHub issue.
/// </summary>
public class GetIssueDetailsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetIssueDetailsSkill>();

    /// <summary>
    /// Gets the details of the specified issue.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue number.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the issue details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Getting issue details for {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var issue = await gitHubClient.Issue.Get(owner, repo, number);

        var result = new
        {
            number = issue.Number,
            title = issue.Title,
            body = issue.Body,
            state = issue.State.StringValue,
            labels = issue.Labels.Select(l => l.Name).ToArray(),
            assignees = issue.Assignees.Select(a => a.Login).ToArray(),
            created_at = issue.CreatedAt,
            updated_at = issue.UpdatedAt,
            html_url = issue.HtmlUrl
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
