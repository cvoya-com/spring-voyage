// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.GraphQL;

using System.Collections;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Wraps Octokit's <see cref="IConnection"/> to issue GraphQL requests against
/// the <c>/graphql</c> endpoint. Serializes variables through
/// <see cref="System.Text.Json"/> (the connector-wide serializer) rather than
/// Octokit's <c>SimpleJsonSerializer</c>, so DTO types can use the same
/// attribute conventions as the rest of the connector.
/// </summary>
/// <remarks>
/// Because the raw response body is still deserialized by Octokit's
/// <c>SimpleJsonSerializer</c> (to a <see cref="JsonElement"/>), this client
/// re-parses the <c>data</c> payload with <c>System.Text.Json</c> using the
/// caller's <typeparamref name="T"/>. GraphQL errors come back in a parallel
/// <c>errors</c> array, which we surface as a
/// <see cref="GitHubGraphQLException"/>.
/// </remarks>
public class OctokitGraphQLClient : IGitHubGraphQLClient
{
    private static readonly Uri GraphQLUri = new("graphql", UriKind.Relative);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConnection _connection;
    private readonly ILogger<OctokitGraphQLClient> _logger;

    /// <summary>
    /// Initializes a new client that issues GraphQL requests through the
    /// given Octokit connection.
    /// </summary>
    public OctokitGraphQLClient(IConnection connection, ILoggerFactory loggerFactory)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = loggerFactory.CreateLogger<OctokitGraphQLClient>();
    }

    /// <inheritdoc />
    public Task<T> QueryAsync<T>(string query, object? variables, CancellationToken cancellationToken = default)
        => SendAsync<T>(query, variables, isMutation: false, cancellationToken);

    /// <inheritdoc />
    public Task<T> MutateAsync<T>(string mutation, object? variables, CancellationToken cancellationToken = default)
        => SendAsync<T>(mutation, variables, isMutation: true, cancellationToken);

    private async Task<T> SendAsync<T>(string query, object? variables, bool isMutation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("GraphQL query/mutation string must be non-empty.", nameof(query));
        }

        _logger.LogDebug(
            "Sending GitHub GraphQL {Kind} (query length {Length})",
            isMutation ? "mutation" : "query",
            query.Length);

        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = NormalizeVariables(variables),
        };

        var response = await _connection.Post<JsonElement>(
            uri: GraphQLUri,
            body: payload,
            accepts: "application/json",
            contentType: "application/json",
            parameters: (IDictionary<string, string>?)null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var body = response.Body;

        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? string.Empty
                    : string.Empty)
                .Where(m => !string.IsNullOrEmpty(m))
                .ToArray();

            throw new GitHubGraphQLException(messages.Length == 0 ? ["unknown GraphQL error"] : messages);
        }

        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("data", out var data))
        {
            throw new GitHubGraphQLException(["GraphQL response missing 'data' field"]);
        }

        // Allow skills to opt into the raw envelope by asking for JsonElement
        // directly; otherwise deserialize the data payload.
        if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)data;
        }

        var result = data.Deserialize<T>(SerializerOptions);
        if (result is null)
        {
            throw new GitHubGraphQLException(["GraphQL 'data' field deserialized to null"]);
        }

        return result;
    }

    /// <summary>
    /// Variables come in as an anonymous object or dictionary. Both work as
    /// the <c>body</c> payload, but dictionaries preserve iteration order and
    /// let callers pass <c>null</c>. Anonymous objects are converted to
    /// dictionaries via System.Text.Json roundtrip to avoid letting Octokit's
    /// SimpleJsonSerializer apply its own casing rules.
    /// </summary>
    private static object NormalizeVariables(object? variables)
    {
        if (variables is null)
        {
            return new Dictionary<string, object?>();
        }

        if (variables is IDictionary dict)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is string key)
                {
                    result[key] = entry.Value;
                }
            }
            return result;
        }

        // Fall back to reflecting properties. We keep property names as-is:
        // GraphQL variable names are case-sensitive and must match $varName
        // declarations exactly.
        var props = variables.GetType().GetProperties();
        var reflected = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in props)
        {
            if (prop.CanRead)
            {
                reflected[prop.Name] = prop.GetValue(variables);
            }
        }
        return reflected;
    }
}