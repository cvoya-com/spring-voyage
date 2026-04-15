// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using System.Collections.Concurrent;

/// <summary>
/// OSS default <see cref="IRateLimitStateStore"/> implementation. Holds
/// snapshots in a thread-safe in-process dictionary keyed by
/// <c>(installationKey, resource)</c>. Matches the pre-persistence
/// behavior of <see cref="GitHubRateLimitTracker"/> — state does not
/// survive restart, and multi-replica deployments do not converge —
/// so this is the right pick only for single-host development and
/// deployments that don't care about cross-replica quota sharing.
/// </summary>
public sealed class InMemoryRateLimitStateStore : IRateLimitStateStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RateLimitSnapshot>> _byInstallation =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<RateLimitSnapshot?> ReadAsync(
        string resource,
        string installationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);

        if (_byInstallation.TryGetValue(installationKey, out var perResource)
            && perResource.TryGetValue(resource, out var snapshot))
        {
            return Task.FromResult<RateLimitSnapshot?>(snapshot);
        }

        return Task.FromResult<RateLimitSnapshot?>(null);
    }

    /// <inheritdoc />
    public Task WriteAsync(
        string resource,
        string installationKey,
        RateLimitSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);
        ArgumentNullException.ThrowIfNull(snapshot);

        var perResource = _byInstallation.GetOrAdd(
            installationKey,
            _ => new ConcurrentDictionary<string, RateLimitSnapshot>(StringComparer.Ordinal));
        perResource[resource] = snapshot;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, RateLimitSnapshot>> ReadAllAsync(
        string installationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationKey);

        if (!_byInstallation.TryGetValue(installationKey, out var perResource))
        {
            return Task.FromResult<IReadOnlyDictionary<string, RateLimitSnapshot>>(
                new Dictionary<string, RateLimitSnapshot>(StringComparer.Ordinal));
        }

        // Snapshot the dictionary so the caller gets a stable view.
        var snapshot = new Dictionary<string, RateLimitSnapshot>(perResource, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, RateLimitSnapshot>>(snapshot);
    }
}