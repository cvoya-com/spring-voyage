// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

using System.Text.Json;

using Microsoft.Extensions.Logging;

/// <summary>
/// Small read-through wrapper used by <see cref="GitHubSkillRegistry"/> to
/// add caching to a subset of skills without fanning the concern out across
/// every individual skill class. Callers supply the cache key plus a factory
/// that produces the underlying skill result when the cache misses; the
/// invoker handles the read, the write, and the diagnostic hit / miss logs.
/// </summary>
/// <remarks>
/// Keeping the wrapper out of the skill classes themselves means the
/// cache-free execution path (tests that construct a skill directly) stays
/// identical to today's behaviour. Only the registry's dispatcher table
/// sees the cache — a skill invoked directly from a test bypasses it.
/// </remarks>
public sealed class CachedSkillInvoker
{
    private readonly IGitHubResponseCache _cache;
    private readonly GitHubResponseCacheOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes the invoker with the shared cache, the TTL lookup
    /// options, and a logger for diagnostic hit / miss output.
    /// </summary>
    public CachedSkillInvoker(
        IGitHubResponseCache cache,
        GitHubResponseCacheOptions options,
        ILoggerFactory loggerFactory)
    {
        _cache = cache;
        _options = options;
        _logger = loggerFactory.CreateLogger<CachedSkillInvoker>();
    }

    /// <summary>
    /// Runs <paramref name="factory"/> under the cache. On hit, returns the
    /// cached <see cref="JsonElement"/> immediately — the underlying Octokit
    /// call (and the rate-limit decrement that rides on its headers) is
    /// skipped entirely. On miss, invokes <paramref name="factory"/>, stores
    /// the result under the resource's configured TTL, and returns it.
    /// </summary>
    /// <param name="resource">Resource identifier (see <see cref="GitHubResponseCacheOptions.Resources"/>) — drives the TTL lookup.</param>
    /// <param name="discriminator">Scope-qualifying identity for the key (e.g. <c>owner/repo#42</c>).</param>
    /// <param name="tags">Tag names the entry should be indexed under for webhook-driven bulk invalidation.</param>
    /// <param name="factory">Produces the <see cref="JsonElement"/> result when the cache misses.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<JsonElement> InvokeAsync(
        string resource,
        string discriminator,
        IReadOnlyList<string> tags,
        Func<CancellationToken, Task<JsonElement>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var key = new CacheKey(resource, discriminator, tags);

        var hit = await _cache.TryGetAsync<JsonElement>(key, cancellationToken).ConfigureAwait(false);
        if (hit.HasValue)
        {
            _logger.LogDebug(
                "Cache hit for GitHub skill result {Resource}:{Discriminator} age={Age}",
                resource, discriminator, hit.Value.Age);
            return hit.Value.Value;
        }

        _logger.LogDebug(
            "Cache miss for GitHub skill result {Resource}:{Discriminator} — invoking upstream",
            resource, discriminator);

        var result = await factory(cancellationToken).ConfigureAwait(false);

        var ttl = _options.ResolveTtl(resource);
        await _cache.SetAsync(key, result, ttl, cancellationToken).ConfigureAwait(false);

        return result;
    }
}