// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IGitHubRateLimitTracker"/> implementation that parses
/// GitHub's <c>x-ratelimit-*</c> response headers, caches the latest
/// quota per resource bucket, and offers a preflight
/// <see cref="WaitIfNeededAsync"/> hook that callers plug in before
/// consuming quota-sensitive endpoints.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IRateLimitStateStore"/> is used to persist each
/// snapshot as it is observed and to seed the in-memory view at
/// startup. The hot path still reads from the in-memory dictionary;
/// the store is a write-through layer that lets a restart or a sibling
/// replica learn the current quota immediately rather than after the
/// next real GitHub response.
/// </para>
/// <para>
/// <b>Failure policy.</b> Persistence failures must never block a real
/// request. When the store's <see cref="IRateLimitStateStore.WriteAsync"/>
/// throws, the tracker logs at warning and continues with its
/// in-memory view; the next successful response will attempt to
/// persist the refreshed snapshot again.
/// </para>
/// </remarks>
public class GitHubRateLimitTracker : IGitHubRateLimitTracker
{
    private readonly ConcurrentDictionary<string, RateLimitQuota> _quotas = new(StringComparer.OrdinalIgnoreCase);
    private readonly GitHubRetryOptions _options;
    private readonly IRateLimitStateStore _stateStore;
    private readonly RateLimitStateStoreOptions _stateStoreOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubRateLimitTracker> _logger;

    /// <summary>
    /// Legacy constructor that defaults the state store to the OSS
    /// in-memory implementation. Kept for source-compat with existing
    /// tests that instantiate the tracker directly without a store.
    /// </summary>
    public GitHubRateLimitTracker(
        GitHubRetryOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
        : this(
            options,
            new InMemoryRateLimitStateStore(),
            Options.Create(new RateLimitStateStoreOptions()),
            loggerFactory,
            timeProvider)
    {
    }

    /// <summary>
    /// Full constructor. <paramref name="stateStore"/> persists every
    /// observation and is consulted at
    /// <see cref="SeedFromStateStoreAsync(CancellationToken)"/> time.
    /// </summary>
    public GitHubRateLimitTracker(
        GitHubRetryOptions options,
        IRateLimitStateStore stateStore,
        IOptions<RateLimitStateStoreOptions> stateStoreOptions,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _stateStoreOptions = stateStoreOptions?.Value ?? throw new ArgumentNullException(nameof(stateStoreOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<GitHubRateLimitTracker>();
    }

    /// <inheritdoc />
    public RateLimitQuota? GetQuota(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        return _quotas.TryGetValue(resource, out var quota) ? quota : null;
    }

    /// <inheritdoc />
    public void UpdateFromHeaders(HttpResponseHeaders responseHeaders)
    {
        ArgumentNullException.ThrowIfNull(responseHeaders);

        if (!TryGetHeaderInt(responseHeaders, "x-ratelimit-limit", out var limit) ||
            !TryGetHeaderInt(responseHeaders, "x-ratelimit-remaining", out var remaining) ||
            !TryGetHeaderLong(responseHeaders, "x-ratelimit-reset", out var resetEpoch))
        {
            return;
        }

        var resource = GetHeaderString(responseHeaders, "x-ratelimit-resource") ?? "core";
        var now = _timeProvider.GetUtcNow();
        var quota = new RateLimitQuota(
            Resource: resource,
            Limit: limit,
            Remaining: remaining,
            Reset: DateTimeOffset.FromUnixTimeSeconds(resetEpoch),
            ObservedAt: now);

        _quotas[resource] = quota;

        // Persist through to the state store. Awaited via blocking
        // sync call so a sibling replica or a caller after a restart
        // sees consistent state on the next read; failures are absorbed
        // so the hot path is never blocked by a degraded persistence
        // layer. We use the synchronous bridge here because
        // UpdateFromHeaders is called from DelegatingHandler.SendAsync
        // which is already async — but the tracker contract keeps the
        // observation synchronous so existing Octokit-level tests
        // (that call UpdateFromHeaders directly from a [Fact]) don't
        // need to await.
        try
        {
            var snapshot = new RateLimitSnapshot(
                Remaining: quota.Remaining,
                Limit: quota.Limit,
                ResetAt: quota.Reset,
                UpdatedAt: now);
            _stateStore
                .WriteAsync(resource, _stateStoreOptions.DefaultInstallationKey, snapshot, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist GitHub rate-limit snapshot for resource {Resource}; continuing in-memory",
                resource);
        }
    }

    /// <inheritdoc />
    public async Task WaitIfNeededAsync(string resource, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return;
        }

        if (!_quotas.TryGetValue(resource, out var quota))
        {
            return;
        }

        if (quota.Remaining > _options.PreflightSafetyThreshold)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var wait = quota.Reset - now;
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        if (_options.MaxBackoff > TimeSpan.Zero && wait > _options.MaxBackoff)
        {
            wait = _options.MaxBackoff;
        }

        _logger.LogInformation(
            "Preflight wait for GitHub resource {Resource}: remaining={Remaining} <= threshold={Threshold}, sleeping {WaitSeconds:0.00}s until reset",
            resource,
            quota.Remaining,
            _options.PreflightSafetyThreshold,
            wait.TotalSeconds);

        await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SeedFromStateStoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, RateLimitSnapshot> persisted;
        try
        {
            persisted = await _stateStore
                .ReadAllAsync(_stateStoreOptions.DefaultInstallationKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to seed GitHub rate-limit tracker from state store; continuing with empty in-memory view");
            return;
        }

        foreach (var (resource, snapshot) in persisted)
        {
            var seeded = new RateLimitQuota(
                Resource: resource,
                Limit: snapshot.Limit,
                Remaining: snapshot.Remaining,
                Reset: snapshot.ResetAt,
                ObservedAt: snapshot.UpdatedAt);

            // Only seed when no in-memory entry is newer — local header
            // observations always win over persisted snapshots so a
            // racey seed can never roll back fresh data.
            _quotas.AddOrUpdate(
                resource,
                _ => seeded,
                (_, existing) => existing.ObservedAt >= seeded.ObservedAt ? existing : seeded);
        }

        _logger.LogInformation(
            "Seeded GitHub rate-limit tracker with {Count} persisted resource snapshot(s)",
            persisted.Count);
    }

    private static bool TryGetHeaderInt(HttpResponseHeaders headers, string name, out int value)
    {
        var raw = GetHeaderString(headers, name);
        if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetHeaderLong(HttpResponseHeaders headers, string name, out long value)
    {
        var raw = GetHeaderString(headers, name);
        if (raw is not null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string? GetHeaderString(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        using var enumerator = values.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }
}