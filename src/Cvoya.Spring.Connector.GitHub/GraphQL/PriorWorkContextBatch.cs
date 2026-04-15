// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Globalization;
using System.Text.Json.Serialization;

/// <summary>
/// Batches the four bucket searches that back the
/// <c>github_get_prior_work_context</c> skill — mentions, authored PRs,
/// commented issues, assigned issues — into a single GraphQL request.
/// Saves three round trips per call and lets the rate-limit tracker
/// decrement the <c>graphql</c> bucket once rather than the <c>search</c>
/// bucket four times.
/// </summary>
/// <remarks>
/// <para>
/// Each bucket is expressed as a GraphQL <c>search(type: ISSUE, query: "..." first: N)</c>
/// field with the appropriate qualifier (<c>mentions:</c>, <c>author:</c>,
/// <c>commenter:</c>, <c>assignee:</c>). Results are merged into a
/// single <see cref="PriorWorkContextResult"/>; partial failures in a
/// single bucket surface as <see cref="PriorWorkBucket.Error"/> without
/// poisoning the other buckets (matching <see cref="GraphQLBatch"/>'s
/// per-alias error semantics).
/// </para>
/// <para>
/// The returned shape is deliberately flat and JSON-serializable so the
/// skill can project it into the same tool-surface shape as the
/// pre-migration REST path.
/// </para>
/// </remarks>
public static class PriorWorkContextBatch
{
    /// <summary>Aliases used for each bucket; stable for test assertions.</summary>
    public const string MentionsAlias = "mentions_search";

    /// <inheritdoc cref="MentionsAlias"/>
    public const string AuthoredAlias = "authored_search";

    /// <inheritdoc cref="MentionsAlias"/>
    public const string CommentedAlias = "commented_search";

    /// <inheritdoc cref="MentionsAlias"/>
    public const string AssignedAlias = "assigned_search";

    /// <summary>
    /// Executes the four-bucket prior-work batch for the given user in
    /// <paramref name="owner"/>/<paramref name="repo"/>. <paramref name="perBucket"/>
    /// is passed to each bucket's <c>first:</c> argument; callers are
    /// expected to clamp it to [1, 100] because the GitHub GraphQL
    /// search field caps at 100.
    /// </summary>
    public static async Task<PriorWorkContextResult> ExecuteAsync(
        IGitHubGraphQLClient client,
        string owner,
        string repo,
        string user,
        DateTimeOffset? since,
        int perBucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        if (perBucket < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(perBucket), "perBucket must be >= 1.");
        }

        var batch = new GraphQLBatch();
        batch.Add<SearchResult>(MentionsAlias, BuildSearchBody($"mentions:{user}", owner, repo, since, perBucket));
        batch.Add<SearchResult>(AuthoredAlias, BuildSearchBody($"author:{user} is:pr", owner, repo, since, perBucket));
        batch.Add<SearchResult>(CommentedAlias, BuildSearchBody($"commenter:{user} is:issue", owner, repo, since, perBucket));
        batch.Add<SearchResult>(AssignedAlias, BuildSearchBody($"assignee:{user} is:issue", owner, repo, since, perBucket));

        var result = await batch.ExecuteAsync(client, cancellationToken).ConfigureAwait(false);

        return new PriorWorkContextResult(
            Mentions: Extract(result, MentionsAlias),
            Authored: Extract(result, AuthoredAlias),
            Commented: Extract(result, CommentedAlias),
            Assigned: Extract(result, AssignedAlias));
    }

    private static PriorWorkBucket Extract(GraphQLBatch.GraphQLBatchResult result, string alias)
    {
        if (!result.TryGet<SearchResult>(alias, out var search, out var error))
        {
            return new PriorWorkBucket(Items: [], Error: error);
        }

        var nodes = search?.Nodes ?? [];
        var items = nodes
            .Where(n => n is not null)
            .Select(n => new PriorWorkItem(
                Url: n!.Url ?? string.Empty,
                Type: ClassifyType(n),
                Number: n.Number,
                Title: n.Title ?? string.Empty,
                State: n.State ?? string.Empty,
                Author: n.Author?.Login,
                CreatedAt: n.CreatedAt,
                UpdatedAt: n.UpdatedAt))
            .ToArray();

        return new PriorWorkBucket(Items: items, Error: null);
    }

    private static string ClassifyType(SearchResultNode node) =>
        string.Equals(node.Typename, "PullRequest", StringComparison.Ordinal) ? "pull_request" : "issue";

    private static string BuildSearchBody(
        string qualifier,
        string owner,
        string repo,
        DateTimeOffset? since,
        int perBucket)
    {
        // Build the GitHub search query. "repo:o/r" scopes to the target
        // repo; "updated:>ISO" replicates the REST path's since filter.
        var query = new System.Text.StringBuilder();
        query.Append("repo:").Append(owner).Append('/').Append(repo).Append(' ').Append(qualifier);
        if (since is { } sinceValue)
        {
            query.Append(" updated:>")
                .Append(sinceValue.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        // The search field returns a union; we ask for issue-specific
        // fields in an inline fragment and a type discriminator via
        // __typename. Inline-literal the query and page size so the
        // outer batch doesn't have to rewrite variables across entries.
        return string.Format(
            CultureInfo.InvariantCulture,
            """
            search(type: ISSUE, query: "{0}", first: {1}) {{
              nodes {{
                __typename
                ... on Issue {{ number title url state createdAt updatedAt author {{ login }} }}
                ... on PullRequest {{ number title url state createdAt updatedAt author {{ login }} }}
              }}
            }}
            """,
            EscapeGraphQLString(query.ToString()),
            perBucket);
    }

    private static string EscapeGraphQLString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>GraphQL <c>search</c> connection envelope.</summary>
    public sealed record SearchResult(
        [property: JsonPropertyName("nodes")] IReadOnlyList<SearchResultNode?> Nodes);

    /// <summary>
    /// Union node returned by the <c>search(type: ISSUE)</c> field —
    /// Issue and PullRequest share the fields we fetch. The GitHub
    /// GraphQL state enum is mapped to a lowercase string through the
    /// <c>state</c> field's inline fragment projection at query time,
    /// hence <see cref="State"/> is a <c>string?</c>.
    /// </summary>
    public sealed record SearchResultNode(
        [property: JsonPropertyName("__typename")] string? Typename,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("author")] PriorWorkAuthor? Author);

    /// <summary>Author projection for a search result.</summary>
    public sealed record PriorWorkAuthor(
        [property: JsonPropertyName("login")] string Login);

    /// <summary>Bucket result — items plus optional per-bucket error.</summary>
    public sealed record PriorWorkBucket(IReadOnlyList<PriorWorkItem> Items, string? Error);

    /// <summary>Single item in a prior-work bucket.</summary>
    public sealed record PriorWorkItem(
        string Url,
        string Type,
        int Number,
        string Title,
        string State,
        string? Author,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? UpdatedAt);

    /// <summary>Full prior-work context response — one bucket per topic.</summary>
    public sealed record PriorWorkContextResult(
        PriorWorkBucket Mentions,
        PriorWorkBucket Authored,
        PriorWorkBucket Commented,
        PriorWorkBucket Assigned);
}