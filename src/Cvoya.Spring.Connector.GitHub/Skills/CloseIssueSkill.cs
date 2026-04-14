// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Closes an existing GitHub issue, optionally recording a close reason.
/// </summary>
public class CloseIssueSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CloseIssueSkill>();

    /// <summary>
    /// Closes the specified issue.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The issue number.</param>
    /// <param name="reason">
    /// Optional close reason: <c>completed</c>, <c>not_planned</c>, or <c>reopened</c>. If
    /// unspecified or unrecognized, GitHub's default (<c>completed</c>) is used.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element containing the updated issue state.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Closing issue {Owner}/{Repo}#{Number} with reason {Reason}",
            owner, repo, number, reason ?? "completed");

        var update = new IssueUpdate
        {
            State = ItemState.Closed,
        };

        if (TryParseReason(reason, out var parsedReason))
        {
            update.StateReason = parsedReason;
        }

        var issue = await gitHubClient.Issue.Update(owner, repo, number, update);

        var result = new
        {
            number = issue.Number,
            state = issue.State.StringValue,
            state_reason = issue.StateReason?.StringValue,
            html_url = issue.HtmlUrl,
        };

        return JsonSerializer.SerializeToElement(result);
    }

    private static bool TryParseReason(string? reason, out ItemStateReason parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        switch (reason.ToLowerInvariant())
        {
            case "completed":
                parsed = ItemStateReason.Completed;
                return true;
            case "not_planned":
            case "not-planned":
            case "notplanned":
                parsed = ItemStateReason.NotPlanned;
                return true;
            case "reopened":
                parsed = ItemStateReason.Reopened;
                return true;
            default:
                return false;
        }
    }
}