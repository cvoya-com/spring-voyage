// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Reads a file from a GitHub repository.
/// </summary>
public class ReadFileSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ReadFileSkill>();

    /// <summary>
    /// Reads the contents of a file from the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="path">The file path within the repository.</param>
    /// <param name="gitRef">An optional Git reference (branch, tag, or SHA). Defaults to the repository's default branch.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the file content and metadata.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string path,
        string? gitRef = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Reading file {Path} from {Owner}/{Repo} at ref {Ref}",
            path, owner, repo, gitRef ?? "default");

        var contents = string.IsNullOrEmpty(gitRef)
            ? await gitHubClient.Repository.Content.GetAllContents(owner, repo, path)
            : await gitHubClient.Repository.Content.GetAllContentsByRef(owner, repo, path, gitRef);

        var file = contents[0];

        var result = new
        {
            name = file.Name,
            path = file.Path,
            sha = file.Sha,
            size = file.Size,
            content = file.Content,
            encoding = file.EncodedContent != null ? "base64" : "utf-8"
        };

        return JsonSerializer.SerializeToElement(result);
    }
}