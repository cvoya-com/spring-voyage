// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Dapr state-store-backed <see cref="IRateLimitStateStore"/>. Writes
/// one row per <c>(installationKey, resource)</c> plus a small index row
/// per installation so <see cref="ReadAllAsync"/> doesn't require
/// prefix-listing (which the Dapr state building block does not expose).
/// </summary>
/// <remarks>
/// <para>
/// <b>Key shape.</b> <c>{KeyPrefix}gh-ratelimit/{installationKey}/{resource}</c>
/// for individual snapshots; <c>{KeyPrefix}gh-ratelimit/{installationKey}/_index</c>
/// for the per-installation resource index. Keys are URL-safe; the
/// resource name is emitted as-is (GitHub uses lowercase ASCII identifiers
/// such as <c>core</c>, <c>graphql</c>, <c>search</c>).
/// </para>
/// <para>
/// <b>Component selection.</b> Follows the convention used by
/// <c>SecretsOptions.ComponentNameFormat</c> — when the format string
/// contains <c>{installationKey}</c>, the backing Dapr component is
/// resolved per-installation. Rate-limit state does not require
/// isolation, but supporting the pattern keeps deployments consistent.
/// </para>
/// <para>
/// <b>Error handling.</b> Implementations of
/// <see cref="IRateLimitStateStore"/> must surface failures to the
/// tracker, which logs at warning and continues in-memory. This class
/// does <b>not</b> catch Dapr exceptions — the tracker is the single
/// circuit-breaker point.
/// </para>
/// </remarks>
public class DaprStateBackedRateLimitStateStore : IRateLimitStateStore
{
    private const string KeyNamespace = "gh-ratelimit";
    private const string IndexKeySuffix = "_index";

    private readonly DaprClient _daprClient;
    private readonly IOptions<RateLimitStateStoreOptions> _options;
    private readonly ILogger<DaprStateBackedRateLimitStateStore> _logger;

    /// <summary>Creates a new <see cref="DaprStateBackedRateLimitStateStore"/>.</summary>
    public DaprStateBackedRateLimitStateStore(
        DaprClient daprClient,
        IOptions<RateLimitStateStoreOptions> options,
        ILogger<DaprStateBackedRateLimitStateStore> logger)
    {
        _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<RateLimitSnapshot?> ReadAsync(
        string resource,
        string installationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);

        var component = ResolveComponent(installationKey);
        var key = BuildSnapshotKey(installationKey, resource);

        _logger.LogDebug(
            "Reading rate-limit snapshot from component {Component} under key {Key}",
            component, key);

        var stored = await _daprClient
            .GetStateAsync<RateLimitSnapshot?>(component, key, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return stored;
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        string resource,
        string installationKey,
        RateLimitSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);
        ArgumentNullException.ThrowIfNull(snapshot);

        var component = ResolveComponent(installationKey);
        var snapshotKey = BuildSnapshotKey(installationKey, resource);

        _logger.LogDebug(
            "Writing rate-limit snapshot to component {Component} under key {Key}",
            component, snapshotKey);

        await _daprClient
            .SaveStateAsync(component, snapshotKey, snapshot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await UpdateIndexAsync(component, installationKey, resource, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, RateLimitSnapshot>> ReadAllAsync(
        string installationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);

        var component = ResolveComponent(installationKey);
        var indexKey = BuildIndexKey(installationKey);

        var index = await _daprClient
            .GetStateAsync<ResourceIndex?>(component, indexKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (index is null || index.Resources.Count == 0)
        {
            return new Dictionary<string, RateLimitSnapshot>(StringComparer.Ordinal);
        }

        // Bulk-read the snapshots referenced by the index. Dapr's
        // GetBulkStateAsync is cheaper than N round-trips; when the
        // component doesn't support bulk-get it degrades transparently.
        var keys = index.Resources
            .Select(r => BuildSnapshotKey(installationKey, r))
            .ToList();
        var bulk = await _daprClient
            .GetBulkStateAsync(component, keys, parallelism: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, RateLimitSnapshot>(StringComparer.Ordinal);
        foreach (var entry in bulk)
        {
            if (string.IsNullOrEmpty(entry.Value))
            {
                continue;
            }

            RateLimitSnapshot? snapshot;
            try
            {
                snapshot = System.Text.Json.JsonSerializer.Deserialize<RateLimitSnapshot>(entry.Value);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to deserialize rate-limit snapshot under key {Key}; skipping",
                    entry.Key);
                continue;
            }

            if (snapshot is null)
            {
                continue;
            }

            var resource = ExtractResourceFromKey(entry.Key, installationKey);
            if (resource is not null)
            {
                result[resource] = snapshot;
            }
        }

        return result;
    }

    private async Task UpdateIndexAsync(
        string component,
        string installationKey,
        string resource,
        CancellationToken cancellationToken)
    {
        var indexKey = BuildIndexKey(installationKey);

        // Last-writer-wins on the index. The set of resources a GitHub
        // installation touches is tiny (core / graphql / search / plus
        // a few ephemeral buckets) so races at worst drop one entry
        // which the next write puts back.
        var current = await _daprClient
            .GetStateAsync<ResourceIndex?>(component, indexKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var resources = current is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(current.Resources, StringComparer.Ordinal);

        if (!resources.Add(resource))
        {
            return;
        }

        await _daprClient
            .SaveStateAsync(
                component,
                indexKey,
                new ResourceIndex(resources.ToList()),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private string BuildSnapshotKey(string installationKey, string resource) =>
        string.Concat(_options.Value.KeyPrefix, KeyNamespace, "/", installationKey, "/", resource);

    private string BuildIndexKey(string installationKey) =>
        string.Concat(_options.Value.KeyPrefix, KeyNamespace, "/", installationKey, "/", IndexKeySuffix);

    private string ResolveComponent(string installationKey)
    {
        var format = _options.Value.ComponentNameFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            return _options.Value.StoreComponent;
        }

        return format.Replace("{installationKey}", installationKey, StringComparison.Ordinal);
    }

    private string? ExtractResourceFromKey(string key, string installationKey)
    {
        // Keys look like: {prefix}gh-ratelimit/{installationKey}/{resource}
        var marker = string.Concat(
            _options.Value.KeyPrefix,
            KeyNamespace,
            "/",
            installationKey,
            "/");
        if (!key.StartsWith(marker, StringComparison.Ordinal))
        {
            return null;
        }

        var tail = key[marker.Length..];
        if (string.IsNullOrEmpty(tail) || string.Equals(tail, IndexKeySuffix, StringComparison.Ordinal))
        {
            return null;
        }

        return tail;
    }

    // Invariant ordering is not needed; the index is a set of resource
    // names, serialized as a list for wire compactness.
    internal sealed record ResourceIndex(List<string> Resources);
}