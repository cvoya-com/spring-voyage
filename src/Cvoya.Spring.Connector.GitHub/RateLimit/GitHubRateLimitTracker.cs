// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;

/// <summary>
/// In-memory implementation of <see cref="IGitHubRateLimitTracker"/>. Parses
/// GitHub's <c>x-ratelimit-*</c> response headers, caches the latest quota per
/// resource bucket, and offers a preflight <see cref="WaitIfNeededAsync"/> hook
/// that callers plug in before consuming quota-sensitive endpoints.
/// </summary>
/// <remarks>
/// This implementation is deliberately process-local and stateless across
/// restarts. Cross-replica persistence is a separate follow-up
/// (see issue tracked under the #231 umbrella).
/// </remarks>
public class GitHubRateLimitTracker : IGitHubRateLimitTracker
{
    private readonly ConcurrentDictionary<string, RateLimitQuota> _quotas = new(StringComparer.OrdinalIgnoreCase);
    private readonly GitHubRetryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubRateLimitTracker> _logger;

    public GitHubRateLimitTracker(
        GitHubRetryOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        var quota = new RateLimitQuota(
            Resource: resource,
            Limit: limit,
            Remaining: remaining,
            Reset: DateTimeOffset.FromUnixTimeSeconds(resetEpoch),
            ObservedAt: _timeProvider.GetUtcNow());

        _quotas[resource] = quota;
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