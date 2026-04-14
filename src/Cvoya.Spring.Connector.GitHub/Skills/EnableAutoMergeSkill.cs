// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Enables auto-merge on a pull request so it will be merged automatically once
/// branch-protection checks pass. GitHub exposes this capability only through
/// GraphQL's <c>enablePullRequestAutoMerge</c> mutation. REST is still used to
/// resolve the PR's node id. The GraphQL call goes through
/// <see cref="IGitHubGraphQLClient"/> — the reusable wrapper introduced with
/// the review-thread skills — so callers no longer see raw
/// <see cref="IConnection"/> wiring here.
/// </summary>
public class EnableAutoMergeSkill(IGitHubClient gitHubClient, IGitHubGraphQLClient graphQLClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<EnableAutoMergeSkill>();

    private const string Mutation = """
        mutation EnableAutoMerge($prId: ID!, $mergeMethod: PullRequestMergeMethod!, $headline: String, $body: String) {
          enablePullRequestAutoMerge(input: {
            pullRequestId: $prId,
            mergeMethod: $mergeMethod,
            commitHeadline: $headline,
            commitBody: $body
          }) {
            pullRequest { number autoMergeRequest { enabledAt mergeMethod } }
          }
        }
        """;

    /// <summary>
    /// Enables auto-merge on the specified pull request.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="mergeMethod">Merge strategy to use when auto-merging: <c>merge</c>, <c>squash</c> (default), or <c>rebase</c>.</param>
    /// <param name="commitHeadline">Optional commit headline passed through to GitHub.</param>
    /// <param name="commitBody">Optional commit body passed through to GitHub.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result as a JSON element confirming the auto-merge request.</returns>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        int number,
        string? mergeMethod,
        string? commitHeadline,
        string? commitBody,
        CancellationToken cancellationToken = default)
    {
        var graphqlMergeMethod = ParseMergeMethod(mergeMethod);

        _logger.LogInformation(
            "Enabling auto-merge on {Owner}/{Repo}#{Number} method={Method}",
            owner, repo, number, graphqlMergeMethod);

        var pr = await gitHubClient.PullRequest.Get(owner, repo, number);
        if (string.IsNullOrWhiteSpace(pr.NodeId))
        {
            throw new InvalidOperationException(
                $"PR {owner}/{repo}#{number} has no node id; cannot enable auto-merge.");
        }

        var variables = new Dictionary<string, object?>
        {
            ["prId"] = pr.NodeId,
            ["mergeMethod"] = graphqlMergeMethod,
            ["headline"] = commitHeadline,
            ["body"] = commitBody,
        };

        // We don't need the mutation response body (the call either succeeds
        // or throws GitHubGraphQLException); ask for JsonElement so we don't
        // need a dedicated DTO just to satisfy the type parameter.
        _ = await graphQLClient.MutateAsync<JsonElement>(
            Mutation,
            variables,
            cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            enabled = true,
            number = pr.Number,
            node_id = pr.NodeId,
            merge_method = graphqlMergeMethod,
        });
    }

    private static string ParseMergeMethod(string? mergeMethod) =>
        (mergeMethod?.ToLowerInvariant()) switch
        {
            "merge" => "MERGE",
            "rebase" => "REBASE",
            _ => "SQUASH",
        };
}