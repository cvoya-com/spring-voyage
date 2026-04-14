// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="DelegatingHandler"/> that sits inside the Octokit HTTP pipeline
/// and transparently:
/// <list type="bullet">
///   <item>feeds every response's <c>x-ratelimit-*</c> headers into
///     <see cref="IGitHubRateLimitTracker"/> so later callers can preflight,</item>
///   <item>retries calls that hit GitHub's primary rate limit (403 with
///     <c>x-ratelimit-remaining: 0</c>), secondary rate limit (403 with a
///     <c>Retry-After</c> header or a body mentioning "secondary rate limit"),
///     or 429 Too Many Requests — honouring the server-provided hint over the
///     base exponential backoff,</item>
///   <item>leaves every other 4xx / 5xx response untouched so Octokit can
///     surface the domain error to callers.</item>
/// </list>
/// </summary>
public class GitHubRetryHandler : DelegatingHandler
{
    private readonly IGitHubRateLimitTracker _tracker;
    private readonly GitHubRetryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubRetryHandler> _logger;

    public GitHubRetryHandler(
        IGitHubRateLimitTracker tracker,
        GitHubRetryOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<GitHubRetryHandler>();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= _options.MaxRetries + 1; attempt++)
        {
            response?.Dispose();

            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            _tracker.UpdateFromHeaders(response.Headers);

            if (!ShouldRetry(response, out var reason))
            {
                return response;
            }

            if (attempt > _options.MaxRetries)
            {
                _logger.LogWarning(
                    "GitHub request to {RequestUri} exhausted retries after {Attempts} attempts (last reason: {Reason})",
                    request.RequestUri,
                    attempt,
                    reason);
                return response;
            }

            var delay = ComputeDelay(response, attempt);

            _logger.LogInformation(
                "Retrying GitHub request to {RequestUri} in {DelaySeconds:0.00}s (attempt {Attempt}/{MaxAttempts}, reason: {Reason})",
                request.RequestUri,
                delay.TotalSeconds,
                attempt,
                _options.MaxRetries + 1,
                reason);

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        // Loop always returns inside; defensive fallback to satisfy the compiler.
        return response!;
    }

    private static bool ShouldRetry(HttpResponseMessage response, out string reason)
    {
        if ((int)response.StatusCode == 429)
        {
            reason = "429 Too Many Requests";
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Primary rate limit: remaining=0 and a reset header.
            if (TryGetHeaderString(response.Headers, "x-ratelimit-remaining", out var remaining)
                && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remainingValue)
                && remainingValue <= 0)
            {
                reason = "403 primary rate-limit (x-ratelimit-remaining: 0)";
                return true;
            }

            // Secondary rate limit: 403 + Retry-After header.
            if (response.Headers.RetryAfter is not null)
            {
                reason = "403 secondary rate-limit (Retry-After)";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
    {
        // 1. Retry-After wins (delta seconds or HTTP-date).
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return Cap(delta);
            }

            if (retryAfter.Date is { } date)
            {
                var wait = date - _timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    return Cap(wait);
                }
            }
        }

        // 2. x-ratelimit-reset when primary rate-limited.
        if (TryGetHeaderString(response.Headers, "x-ratelimit-reset", out var resetRaw)
            && long.TryParse(resetRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            var wait = reset - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                return Cap(wait);
            }
        }

        // 3. Exponential backoff: base * 2^(attempt-1).
        var seconds = _options.BaseBackoff.TotalSeconds * Math.Pow(2, attempt - 1);
        return Cap(TimeSpan.FromSeconds(seconds));
    }

    private TimeSpan Cap(TimeSpan wait)
    {
        if (_options.MaxBackoff > TimeSpan.Zero && wait > _options.MaxBackoff)
        {
            return _options.MaxBackoff;
        }

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static bool TryGetHeaderString(HttpResponseHeaders headers, string name, out string value)
    {
        if (headers.TryGetValues(name, out var values))
        {
            using var enumerator = values.GetEnumerator();
            if (enumerator.MoveNext())
            {
                value = enumerator.Current;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}