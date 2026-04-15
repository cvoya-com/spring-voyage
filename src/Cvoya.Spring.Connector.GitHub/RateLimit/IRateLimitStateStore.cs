// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

/// <summary>
/// Persistence abstraction for <see cref="IGitHubRateLimitTracker"/>. The
/// OSS default is process-local in-memory (<see cref="InMemoryRateLimitStateStore"/>),
/// which covers the single-host case. Multi-host deployments substitute an
/// implementation that shares state across replicas (the OSS ships a
/// <see cref="DaprStateBackedRateLimitStateStore"/> that rides on the
/// platform's Dapr state store; the private cloud repo can register its
/// own — Redis-backed, etc. — via DI before calling
/// <c>AddCvoyaSpringConnectorGitHub</c>).
/// </summary>
/// <remarks>
/// <para>
/// Concurrency model is <b>last-writer-wins</b>: concurrent callers can
/// interleave their writes, and the store is not expected to serialize
/// them. Convergence is guaranteed because every real GitHub response
/// refreshes the snapshot with authoritative headers — eventual staleness
/// is cheap to correct.
/// </para>
/// <para>
/// Failures from the underlying store must never propagate out of the
/// tracker's hot path: the tracker catches, logs, and falls back to its
/// in-memory view. Implementations should therefore NOT retry aggressively
/// on their own — surface the failure quickly and let the tracker decide.
/// </para>
/// </remarks>
public interface IRateLimitStateStore
{
    /// <summary>
    /// Reads the persisted quota for <paramref name="resource"/> scoped to
    /// <paramref name="installationKey"/>. Returns <c>null</c> when the
    /// key has never been written.
    /// </summary>
    /// <param name="resource">Rate-limit resource (e.g. <c>core</c>, <c>graphql</c>, <c>search</c>).</param>
    /// <param name="installationKey">Per-installation scope key. Stable across restarts (e.g. the numeric installation id).</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    Task<RateLimitSnapshot?> ReadAsync(
        string resource,
        string installationKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="snapshot"/> as the latest known quota for
    /// <paramref name="resource"/> scoped to <paramref name="installationKey"/>.
    /// Semantics are last-writer-wins; implementations do not need to
    /// coordinate concurrent writers because GitHub's next response
    /// rewrites the value authoritatively.
    /// </summary>
    Task WriteAsync(
        string resource,
        string installationKey,
        RateLimitSnapshot snapshot,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bulk-reads every persisted resource for <paramref name="installationKey"/>.
    /// Used at tracker startup to seed the in-memory view so the first
    /// caller after a restart has a preflight signal, and by dashboards /
    /// diagnostics that want a whole-installation view. Returns an empty
    /// dictionary when nothing is persisted.
    /// </summary>
    Task<IReadOnlyDictionary<string, RateLimitSnapshot>> ReadAllAsync(
        string installationKey,
        CancellationToken cancellationToken);
}