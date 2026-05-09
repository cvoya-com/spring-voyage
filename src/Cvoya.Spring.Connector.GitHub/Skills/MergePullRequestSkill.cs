// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Merges a pull request using the specified strategy.
/// </summary>
public class MergePullRequestSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MergePullRequestSkill>();

    /// <summary>
    /// Merges the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="mergeMethod">Merge strategy: <c>merge</c> (default), <c>squash</c>, or <c>rebase</c>.</param>
    /// <param name="commitTitle">Optional merge commit title. Ignored for <c>rebase</c>.</param>
    /// <param name="commitMessage">Optional merge commit message. Ignored for <c>rebase</c>.</param>
    /// <param name="sha">Optional SHA the PR head must match. If supplied and the PR has advanced, the merge fails.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the merge outcome.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string? mergeMethod,
        string? commitTitle,
        string? commitMessage,
        string? sha,
        CancellationToken cancellationToken = default)
    {
        var method = ParseMergeMethod(mergeMethod);

        var request = new MergePullRequest { MergeMethod = method };
        if (!string.IsNullOrWhiteSpace(commitTitle))
        {
            request.CommitTitle = commitTitle;
        }
        if (!string.IsNullOrWhiteSpace(commitMessage))
        {
            request.CommitMessage = commitMessage;
        }
        if (!string.IsNullOrWhiteSpace(sha))
        {
            request.Sha = sha;
        }

        _logger.LogInformation(
            "Merging {Owner}/{Repo}#{Number} with method={Method}",
            owner, repo, number, method);

        var merge = await gitHubClient.PullRequest.Merge(owner, repo, number, request);

        return JsonSerializer.SerializeToElement(new
        {
            merged = merge.Merged,
            sha = merge.Sha,
            message = merge.Message,
        });
    }

    private static PullRequestMergeMethod ParseMergeMethod(string? mergeMethod) =>
        (mergeMethod?.ToLowerInvariant()) switch
        {
            "squash" => PullRequestMergeMethod.Squash,
            "rebase" => PullRequestMergeMethod.Rebase,
            _ => PullRequestMergeMethod.Merge,
        };
}
