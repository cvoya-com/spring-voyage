// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Retrieves the full detail record for a single pull request.
/// </summary>
public class GetPullRequestSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetPullRequestSkill>();

    /// <summary>
    /// Gets a pull request by number.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the pull request detail.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Getting pull request {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var pr = await gitHubClient.PullRequest.Get(owner, repo, number);

        return JsonSerializer.SerializeToElement(PullRequestProjection.ProjectDetail(pr));
    }
}
