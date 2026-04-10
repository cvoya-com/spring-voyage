// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Retrieves the diff for a GitHub pull request.
/// </summary>
public class GetPullRequestDiffSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetPullRequestDiffSkill>();

    /// <summary>
    /// Gets the file changes for the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the pull request diff details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Getting pull request diff for {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var files = await gitHubClient.PullRequest.Files(owner, repo, number);

        var changes = files.Select(f => new
        {
            filename = f.FileName,
            status = f.Status,
            additions = f.Additions,
            deletions = f.Deletions,
            changes = f.Changes,
            patch = f.Patch
        }).ToArray();

        var result = new
        {
            pull_request_number = number,
            files = changes,
            total_files = changes.Length
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
