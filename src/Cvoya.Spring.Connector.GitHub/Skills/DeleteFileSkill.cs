// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Deletes a file from a GitHub repository on a given branch.
/// </summary>
public class DeleteFileSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DeleteFileSkill>();

    /// <summary>
    /// Deletes <paramref name="path"/> on <paramref name="branch"/>. Looks up
    /// the current blob sha first — GitHub's delete-file API requires it and
    /// making the lookup implicit is friendlier than forcing the caller to
    /// supply one.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="path">The file path within the repository.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="branch">The branch to commit against.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A JSON result with the deletion commit metadata.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string path,
        string message,
        string branch,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Deleting file {Path} on branch {Branch} in {Owner}/{Repo}",
            path, branch, owner, repo);

        var existing = await gitHubClient.Repository.Content
            .GetAllContentsByRef(owner, repo, path, branch);
        var sha = existing.FirstOrDefault()?.Sha
            ?? throw new NotFoundException(
                $"File '{path}' not found on branch '{branch}' of {owner}/{repo}.",
                System.Net.HttpStatusCode.NotFound);

        var request = new DeleteFileRequest(message, sha, branch);
        await gitHubClient.Repository.Content.DeleteFile(owner, repo, path, request);

        var payload = new
        {
            action = "deleted",
            path,
            branch,
            previous_sha = sha,
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}