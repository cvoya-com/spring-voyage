// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Creates a comment on a GitHub issue or pull request.
/// </summary>
public class CommentSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CommentSkill>();

    /// <summary>
    /// Creates a comment on the specified issue or pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue or pull request number.</param>
    /// <param name="body">The comment body text.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the created comment details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string body,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating comment on {Owner}/{Repo}#{Number}",
            owner, repo, number);

        var comment = await gitHubClient.Issue.Comment.Create(owner, repo, number, body);

        var result = new
        {
            id = comment.Id,
            html_url = comment.HtmlUrl,
            created_at = comment.CreatedAt
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
