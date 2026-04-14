// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Creates a new issue in a GitHub repository.
/// </summary>
public class CreateIssueSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CreateIssueSkill>();

    /// <summary>
    /// Creates an issue in the specified repository.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="title">The issue title.</param>
    /// <param name="body">Optional issue body / description.</param>
    /// <param name="labels">Optional labels to apply on creation.</param>
    /// <param name="assignees">Optional GitHub logins to assign.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the created issue details.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string title,
        string? body,
        string[] labels,
        string[] assignees,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating issue '{Title}' in {Owner}/{Repo} with {LabelCount} labels and {AssigneeCount} assignees",
            title, owner, repo, labels.Length, assignees.Length);

        var newIssue = new NewIssue(title);
        if (!string.IsNullOrWhiteSpace(body))
        {
            newIssue.Body = body;
        }
        foreach (var label in labels)
        {
            newIssue.Labels.Add(label);
        }
        foreach (var assignee in assignees)
        {
            newIssue.Assignees.Add(assignee);
        }

        var issue = await gitHubClient.Issue.Create(owner, repo, newIssue);

        var result = new
        {
            number = issue.Number,
            title = issue.Title,
            html_url = issue.HtmlUrl,
            state = issue.State.StringValue,
            labels = issue.Labels.Select(l => l.Name).ToArray(),
            assignees = issue.Assignees.Select(a => a.Login).ToArray(),
        };

        return JsonSerializer.SerializeToElement(result);
    }
}