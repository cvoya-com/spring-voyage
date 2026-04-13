// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Creates or updates a file in a GitHub repository. If the file does not
/// exist at the target branch it is created; otherwise its contents are
/// overwritten with the supplied body.
/// </summary>
public class WriteFileSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WriteFileSkill>();

    /// <summary>
    /// Writes the supplied content to <paramref name="path"/> on
    /// <paramref name="branch"/>, creating the file when absent and updating
    /// it in place when present.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="path">The file path within the repository.</param>
    /// <param name="content">The UTF-8 text content to write.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="branch">The branch to commit against.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A JSON result with the commit metadata.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string path,
        string content,
        string message,
        string branch,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Writing file {Path} on branch {Branch} in {Owner}/{Repo}",
            path, branch, owner, repo);

        string? existingSha = null;
        try
        {
            var existing = await gitHubClient.Repository.Content
                .GetAllContentsByRef(owner, repo, path, branch);
            existingSha = existing.FirstOrDefault()?.Sha;
        }
        catch (NotFoundException)
        {
            // File does not exist yet — fall through to the create path.
        }

        RepositoryContentChangeSet result;
        string action;
        if (existingSha is null)
        {
            var create = new CreateFileRequest(message, content, branch);
            result = await gitHubClient.Repository.Content.CreateFile(owner, repo, path, create);
            action = "created";
        }
        else
        {
            var update = new UpdateFileRequest(message, content, existingSha, branch);
            result = await gitHubClient.Repository.Content.UpdateFile(owner, repo, path, update);
            action = "updated";
        }

        var payload = new
        {
            action,
            path,
            branch,
            commit_sha = result.Commit.Sha,
            content_sha = result.Content.Sha,
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}