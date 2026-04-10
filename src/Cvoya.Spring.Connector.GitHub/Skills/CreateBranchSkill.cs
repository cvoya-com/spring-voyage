// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Creates a Git branch in a GitHub repository from a specified reference.
/// </summary>
public class CreateBranchSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CreateBranchSkill>();

    /// <summary>
    /// Creates a new branch in the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="branchName">The name of the new branch.</param>
    /// <param name="fromRef">The reference (branch name or SHA) to create the branch from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the new branch reference.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string branchName,
        string fromRef,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating branch {BranchName} from {FromRef} in {Owner}/{Repo}",
            branchName, fromRef, owner, repo);

        var sourceRef = await gitHubClient.Git.Reference.Get(owner, repo, $"heads/{fromRef}");
        var newRef = await gitHubClient.Git.Reference.Create(
            owner,
            repo,
            new NewReference($"refs/heads/{branchName}", sourceRef.Object.Sha));

        var result = new
        {
            @ref = newRef.Ref,
            sha = newRef.Object.Sha
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
