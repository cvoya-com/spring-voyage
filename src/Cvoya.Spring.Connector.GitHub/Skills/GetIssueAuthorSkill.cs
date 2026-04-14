// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Retrieves just the author (opener) of a GitHub issue.
/// </summary>
/// <remarks>
/// Exists as a separate, focused skill because the author is frequently the only
/// field needed (e.g., to decide which agent should respond to new feedback). Shipping
/// it separately avoids forcing the full issue-details payload through the planner
/// when a single login is all that's required.
/// </remarks>
public class GetIssueAuthorSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetIssueAuthorSkill>();

    /// <summary>
    /// Gets the author of the specified issue.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue number.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the issue author login.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Getting author of {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var issue = await gitHubClient.Issue.Get(owner, repo, number);

        var result = new
        {
            number = issue.Number,
            author = issue.User?.Login,
        };

        return JsonSerializer.SerializeToElement(result);
    }
}