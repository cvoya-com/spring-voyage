// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Octokit;

/// <summary>
/// Adds and removes labels on a GitHub issue or pull request.
/// </summary>
public class ManageLabelsSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ManageLabelsSkill>();

    /// <summary>
    /// Manages labels on the specified issue or pull request by adding and/or removing labels.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue or pull request number.</param>
    /// <param name="labelsToAdd">Labels to add. May be empty.</param>
    /// <param name="labelsToRemove">Labels to remove. May be empty.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the updated label list.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string[] labelsToAdd,
        string[] labelsToRemove,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Managing labels on {Owner}/{Repo}#{Number}: adding [{Add}], removing [{Remove}]",
            owner, repo, number,
            string.Join(", ", labelsToAdd),
            string.Join(", ", labelsToRemove));

        foreach (var label in labelsToRemove)
        {
            try
            {
                await gitHubClient.Issue.Labels.RemoveFromIssue(owner, repo, number, label);
            }
            catch (NotFoundException)
            {
                _logger.LogDebug("Label {Label} not found on {Owner}/{Repo}#{Number}, skipping removal",
                    label, owner, repo, number);
            }
        }

        IReadOnlyList<Label> currentLabels;

        if (labelsToAdd.Length > 0)
        {
            currentLabels = await gitHubClient.Issue.Labels.AddToIssue(owner, repo, number, labelsToAdd);
        }
        else
        {
            currentLabels = await gitHubClient.Issue.Labels.GetAllForIssue(owner, repo, number);
        }

        var result = new
        {
            labels = currentLabels.Select(l => new { name = l.Name, color = l.Color }).ToArray()
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
