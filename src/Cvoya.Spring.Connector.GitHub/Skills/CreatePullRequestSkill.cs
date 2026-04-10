// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Creates a pull request in a GitHub repository.
/// </summary>
public class CreatePullRequestSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CreatePullRequestSkill>();

    /// <summary>
    /// Creates a new pull request in the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="title">The pull request title.</param>
    /// <param name="body">The pull request body/description.</param>
    /// <param name="head">The head branch containing the changes.</param>
    /// <param name="baseBranch">The base branch to merge into.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the created pull request details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string title,
        string body,
        string head,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating pull request '{Title}' from {Head} to {Base} in {Owner}/{Repo}",
            title, head, baseBranch, owner, repo);

        var pr = await gitHubClient.PullRequest.Create(
            owner,
            repo,
            new NewPullRequest(title, head, baseBranch) { Body = body });

        var result = new
        {
            number = pr.Number,
            title = pr.Title,
            html_url = pr.HtmlUrl,
            state = pr.State.StringValue
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
