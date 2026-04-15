// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging;

/// <summary>
/// Produces a structured "prior work" summary for an agent login in a given
/// repository — mentions directed at the agent, PRs it has authored, and
/// issues it has commented on / is assigned to.
/// </summary>
/// <remarks>
/// Previously fanned out to four <c>Search.SearchIssues</c> REST calls per
/// invocation; as of wave 8 D12 (#262) this is a single batched GraphQL
/// query with four aliased <c>search(type: ISSUE)</c> sub-queries
/// (<see cref="PriorWorkContextBatch"/>). The public tool surface —
/// arguments and response shape — is preserved; callers observe only the
/// latency drop and the single <c>graphql</c> quota decrement (four
/// <c>search</c> decrements before, one <c>graphql</c> decrement after).
/// </remarks>
public class GetPriorWorkContextSkill(
    IGitHubGraphQLClient graphQLClient,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GetPriorWorkContextSkill>();

    /// <summary>
    /// Executes the prior-work summary. Each bucket is independently page-capped
    /// so a noisy bucket cannot starve the others.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="user">The GitHub login whose prior work we are summarizing.</param>
    /// <param name="since">Optional lower bound — only include items updated after this timestamp.</param>
    /// <param name="maxPerBucket">Maximum items per bucket (mentions / authored PRs / commented / assigned). Capped at 100.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        string user,
        DateTimeOffset? since,
        int maxPerBucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);

        var bareLogin = user.StartsWith('@') ? user[1..] : user;
        var limit = Math.Clamp(maxPerBucket, 1, 100);

        _logger.LogInformation(
            "Gathering prior-work context for @{User} in {Owner}/{Repo} (limit {Limit} per bucket) via GraphQL batch",
            bareLogin, owner, repo, limit);

        var batch = await PriorWorkContextBatch
            .ExecuteAsync(graphQLClient, owner, repo, bareLogin, since, limit, cancellationToken)
            .ConfigureAwait(false);

        var summary = new
        {
            user = bareLogin,
            repository = new { owner, repo, full_name = $"{owner}/{repo}" },
            since,
            mentions = Project(batch.Mentions),
            authored_pull_requests = Project(batch.Authored),
            commented_issues = Project(batch.Commented),
            assigned_issues = Project(batch.Assigned),
        };

        return JsonSerializer.SerializeToElement(summary);
    }

    private static object Project(PriorWorkContextBatch.PriorWorkBucket bucket) => new
    {
        count = bucket.Items.Count,
        items = bucket.Items
            .Select(i => new
            {
                url = i.Url,
                type = i.Type,
                number = i.Number,
                title = i.Title,
                state = i.State,
                author = i.Author,
                created_at = i.CreatedAt,
                updated_at = i.UpdatedAt,
            })
            .ToArray(),
        error = bucket.Error,
    };
}