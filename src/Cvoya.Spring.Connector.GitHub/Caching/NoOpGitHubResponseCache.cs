// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// Pass-through <see cref="IGitHubResponseCache"/> implementation. Used when
/// caching is disabled via <c>GitHub:ResponseCache:Enabled=false</c> so skill
/// dispatchers can call the cache unconditionally without branching on an
/// options flag at every site.
/// </summary>
public sealed class NoOpGitHubResponseCache : IGitHubResponseCache
{
    /// <summary>
    /// Shared singleton — the class holds no state so every caller can reuse
    /// the same instance regardless of DI scoping.
    /// </summary>
    public static readonly NoOpGitHubResponseCache Instance = new();

    /// <inheritdoc />
    public Task<CacheEntry<T>?> TryGetAsync<T>(CacheKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult<CacheEntry<T>?>(null);

    /// <inheritdoc />
    public Task SetAsync<T>(CacheKey key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task InvalidateAsync(CacheKey key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task InvalidateByTagAsync(string tag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}