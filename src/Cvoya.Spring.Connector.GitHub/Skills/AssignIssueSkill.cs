// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Adds and/or removes assignees from a GitHub issue or pull request.
/// </summary>
/// <remarks>
/// GitHub's assignees API is additive-by-default: <c>POST /assignees</c> adds, <c>DELETE /assignees</c> removes,
/// and neither replaces the full set. This skill exposes both operations through one call so agents can
/// express "add these, remove those" atomically without chaining two skill invocations.
/// </remarks>
public class AssignIssueSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AssignIssueSkill>();

    /// <summary>
    /// Updates the assignee list on the specified issue.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue or pull request number.</param>
    /// <param name="assigneesToAdd">GitHub logins to add as assignees. May be empty.</param>
    /// <param name="assigneesToRemove">GitHub logins to remove as assignees. May be empty.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the updated assignee list.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string[] assigneesToAdd,
        string[] assigneesToRemove,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Updating assignees on {Owner}/{Repo}#{Number}: adding [{Add}], removing [{Remove}]",
            owner, repo, number,
            string.Join(", ", assigneesToAdd),
            string.Join(", ", assigneesToRemove));

        if (assigneesToAdd.Length > 0)
        {
            await gitHubClient.Issue.Assignee.AddAssignees(owner, repo, number, new AssigneesUpdate(assigneesToAdd));
        }

        Issue updated;
        if (assigneesToRemove.Length > 0)
        {
            updated = await gitHubClient.Issue.Assignee.RemoveAssignees(owner, repo, number, new AssigneesUpdate(assigneesToRemove));
        }
        else
        {
            updated = await gitHubClient.Issue.Get(owner, repo, number);
        }

        var result = new
        {
            number = updated.Number,
            assignees = updated.Assignees.Select(a => a.Login).ToArray(),
        };

        return JsonSerializer.SerializeToElement(result);
    }
}