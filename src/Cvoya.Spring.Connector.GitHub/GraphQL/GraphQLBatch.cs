// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// Accumulates aliased GraphQL sub-queries and executes them as a single
/// combined request. Mirrors the v1 batching ceiling of 80 aliases per
/// call — past that, GitHub's node-count cost limits start biting.
/// </summary>
/// <remarks>
/// <para>
/// This is a foundation primitive: skills that want to collapse N REST calls
/// into one GraphQL call opt in by wrapping their query bodies into a batch,
/// then pulling typed results back out by alias. The helper does not attempt
/// to merge fragment definitions or variable namespaces across sub-queries —
/// each <see cref="Add{T}"/> call contributes its own inline variables.
/// </para>
/// <para>
/// Each alias's result is deserialized independently, so a single failing
/// alias does not poison the whole batch: the <see cref="GraphQLBatchResult"/>
/// exposes per-alias success/failure.
/// </para>
/// </remarks>
public sealed class GraphQLBatch
{
    /// <summary>
    /// Maximum aliases per request. v1 used 80 as a practical ceiling; past
    /// that GitHub's GraphQL node-cost limits start rejecting the call.
    /// </summary>
    public const int DefaultMaxAliases = 80;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<BatchEntry> _entries = [];
    private readonly int _maxAliases;

    /// <summary>
    /// Initializes a new empty batch. <paramref name="maxAliases"/> caps how
    /// many sub-queries can be combined before <see cref="Add{T}"/> throws.
    /// </summary>
    public GraphQLBatch(int maxAliases = DefaultMaxAliases)
    {
        if (maxAliases <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAliases), "maxAliases must be positive.");
        }
        _maxAliases = maxAliases;
    }

    /// <summary>Current number of aliases in the batch.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Adds a sub-query to the batch under a caller-chosen alias. The alias
    /// must be a valid GraphQL name and unique within this batch.
    /// </summary>
    /// <param name="alias">Alias the sub-query's result lands under.</param>
    /// <param name="subQueryBody">
    /// The raw sub-query body starting at the root field, e.g.
    /// <c>repository(owner: "o", name: "r") { pullRequest(number: 1) { title } }</c>.
    /// Inline literal values; the batch does not rewrite variables across
    /// entries.
    /// </param>
    /// <typeparam name="T">The DTO type for this sub-query's <c>data.{alias}</c>.</typeparam>
    public void Add<T>(string alias, string subQueryBody)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Alias must be a non-empty string.", nameof(alias));
        }
        if (!IsValidAlias(alias))
        {
            throw new ArgumentException($"Alias '{alias}' is not a valid GraphQL name (must match /^[_A-Za-z][_A-Za-z0-9]*$/).", nameof(alias));
        }
        if (string.IsNullOrWhiteSpace(subQueryBody))
        {
            throw new ArgumentException("Sub-query body must be non-empty.", nameof(subQueryBody));
        }
        if (_entries.Count >= _maxAliases)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Batch is full: maximum {0} aliases allowed.", _maxAliases));
        }
        if (_entries.Any(e => e.Alias == alias))
        {
            throw new ArgumentException($"Alias '{alias}' is already used in this batch.", nameof(alias));
        }

        _entries.Add(new BatchEntry(alias, subQueryBody.Trim(), typeof(T)));
    }

    /// <summary>Returns the combined query string. Empty batch builds an empty query.</summary>
    public string BuildQuery()
    {
        if (_entries.Count == 0)
        {
            return "query Batch { __typename }";
        }

        var sb = new StringBuilder();
        sb.Append("query Batch {\n");
        foreach (var entry in _entries)
        {
            sb.Append("  ").Append(entry.Alias).Append(": ").Append(entry.SubQueryBody).Append('\n');
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Executes the batch and returns a <see cref="GraphQLBatchResult"/>
    /// from which per-alias typed results can be pulled via
    /// <see cref="GraphQLBatchResult.Get{T}"/>.
    /// </summary>
    public async Task<GraphQLBatchResult> ExecuteAsync(IGitHubGraphQLClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var query = BuildQuery();
        var data = await client.QueryAsync<JsonElement>(query, variables: null, cancellationToken).ConfigureAwait(false);

        var perAlias = new Dictionary<string, (JsonElement? Value, string? Error)>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(entry.Alias, out var aliasValue))
            {
                perAlias[entry.Alias] = (aliasValue.Clone(), null);
            }
            else
            {
                perAlias[entry.Alias] = (null, $"alias '{entry.Alias}' missing from response");
            }
        }

        return new GraphQLBatchResult(perAlias);
    }

    private static bool IsValidAlias(string alias)
    {
        if (alias.Length == 0)
        {
            return false;
        }
        var c = alias[0];
        if (c != '_' && !char.IsLetter(c))
        {
            return false;
        }
        for (var i = 1; i < alias.Length; i++)
        {
            c = alias[i];
            if (c != '_' && !char.IsLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private sealed record BatchEntry(string Alias, string SubQueryBody, Type ResultType);

    /// <summary>
    /// Result of a <see cref="GraphQLBatch"/> execution. Use <see cref="Get{T}"/>
    /// to pull typed values per alias; missing aliases throw.
    /// </summary>
    public sealed class GraphQLBatchResult
    {
        private readonly Dictionary<string, (JsonElement? Value, string? Error)> _perAlias;

        internal GraphQLBatchResult(Dictionary<string, (JsonElement? Value, string? Error)> perAlias)
        {
            _perAlias = perAlias;
        }

        /// <summary>The aliases included in this batch result.</summary>
        public IReadOnlyCollection<string> Aliases => _perAlias.Keys;

        /// <summary>
        /// Extracts the typed result for <paramref name="alias"/>. Throws if
        /// the alias is missing or GitHub didn't return data for it; callers
        /// that need tolerant lookup should use <see cref="TryGet{T}"/>.
        /// </summary>
        public T Get<T>(string alias)
        {
            if (!_perAlias.TryGetValue(alias, out var entry))
            {
                throw new KeyNotFoundException($"Alias '{alias}' was not included in this batch.");
            }
            if (entry.Error is not null || entry.Value is not { } value)
            {
                throw new GitHubGraphQLException([entry.Error ?? $"alias '{alias}' missing from response"]);
            }
            if (typeof(T) == typeof(JsonElement))
            {
                return (T)(object)value;
            }
            var result = value.Deserialize<T>(SerializerOptions);
            return result ?? throw new GitHubGraphQLException([$"alias '{alias}' deserialized to null"]);
        }

        /// <summary>
        /// Tolerant lookup: returns <c>false</c> and an error message if the
        /// alias is missing; otherwise deserializes into <typeparamref name="T"/>.
        /// </summary>
        public bool TryGet<T>(string alias, out T? value, out string? error)
        {
            if (!_perAlias.TryGetValue(alias, out var entry))
            {
                value = default;
                error = $"alias '{alias}' was not included in this batch";
                return false;
            }
            if (entry.Error is not null || entry.Value is not { } element)
            {
                value = default;
                error = entry.Error ?? $"alias '{alias}' missing from response";
                return false;
            }
            value = typeof(T) == typeof(JsonElement)
                ? (T)(object)element
                : element.Deserialize<T>(SerializerOptions);
            error = null;
            return true;
        }
    }
}