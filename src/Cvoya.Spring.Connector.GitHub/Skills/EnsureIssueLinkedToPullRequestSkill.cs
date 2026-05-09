// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Ensures a pull request body contains a GitHub closing keyword referencing each
/// of the given issue numbers, appending <c>Closes #N</c> lines when missing.
/// This is the v1 <c>ensure_issue_linked_to_pr</c> helper used after PR creation
/// or update to guarantee issues auto-close on merge.
/// </summary>
public class EnsureIssueLinkedToPullRequestSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private static readonly string[] CloseKeywords = new[]
    {
        "close", "closes", "closed",
        "fix", "fixes", "fixed",
        "resolve", "resolves", "resolved",
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<EnsureIssueLinkedToPullRequestSkill>();

    /// <summary>
    /// Adds missing closing keywords to a PR body so the given issues auto-close on merge.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="issueNumbers">The issue numbers that should be auto-closed by this PR.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element describing which links were appended.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        int[] issueNumbers,
        CancellationToken cancellationToken = default)
    {
        if (issueNumbers.Length == 0)
        {
            throw new ArgumentException("At least one issue number is required.", nameof(issueNumbers));
        }

        _logger.LogInformation(
            "Ensuring {Owner}/{Repo}#{Number} closes issues [{Issues}]",
            owner, repo, number, string.Join(", ", issueNumbers));

        var pr = await gitHubClient.PullRequest.Get(owner, repo, number);
        var body = pr.Body ?? string.Empty;

        var alreadyLinked = issueNumbers
            .Where(n => ContainsClosingKeyword(body, n))
            .ToArray();

        var toAdd = issueNumbers.Except(alreadyLinked).ToArray();

        if (toAdd.Length == 0)
        {
            return JsonSerializer.SerializeToElement(new
            {
                updated = false,
                number = pr.Number,
                already_linked = alreadyLinked,
                appended = Array.Empty<int>(),
            });
        }

        var appendedLines = string.Join("\n", toAdd.Select(n => $"Closes #{n}"));
        var suffix = string.IsNullOrWhiteSpace(body) ? appendedLines : "\n\n" + appendedLines;

        var update = new PullRequestUpdate
        {
            Body = body + suffix,
        };

        var updated = await gitHubClient.PullRequest.Update(owner, repo, number, update);

        return JsonSerializer.SerializeToElement(new
        {
            updated = true,
            number = updated.Number,
            already_linked = alreadyLinked,
            appended = toAdd,
            html_url = updated.HtmlUrl,
        });
    }

    private static bool ContainsClosingKeyword(string body, int issueNumber)
    {
        var pattern = new Regex(
            $@"\b({string.Join("|", CloseKeywords)})\s+#{issueNumber}\b",
            RegexOptions.IgnoreCase);
        return pattern.IsMatch(body);
    }
}
