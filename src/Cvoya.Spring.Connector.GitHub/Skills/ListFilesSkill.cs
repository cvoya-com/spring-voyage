// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists files in a directory within a GitHub repository.
/// </summary>
public class ListFilesSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListFilesSkill>();

    /// <summary>
    /// Lists the contents of a directory in the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="path">The directory path within the repository.</param>
    /// <param name="gitRef">An optional Git reference (branch, tag, or SHA). Defaults to the repository's default branch.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the directory listing.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string path,
        string? gitRef = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Listing files at {Path} in {Owner}/{Repo} at ref {Ref}",
            path, owner, repo, gitRef ?? "default");

        var contents = string.IsNullOrEmpty(gitRef)
            ? await gitHubClient.Repository.Content.GetAllContents(owner, repo, path)
            : await gitHubClient.Repository.Content.GetAllContentsByRef(owner, repo, path, gitRef);

        var items = contents.Select(c => new
        {
            name = c.Name,
            path = c.Path,
            type = c.Type.StringValue,
            size = c.Size,
            sha = c.Sha
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { items });
    }
}