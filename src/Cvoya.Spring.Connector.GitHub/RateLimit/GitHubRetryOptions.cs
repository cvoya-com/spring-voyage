// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

/// <summary>
/// Options controlling the retry + rate-limit behaviour around Octokit. Values
/// default to the numbers used by the v1 Python connector so parity migration
/// is a drop-in.
/// </summary>
public sealed class GitHubRetryOptions
{
    /// <summary>
    /// Maximum number of retry attempts after the initial request. A value of
    /// <c>3</c> means up to four total HTTP calls per logical operation.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base backoff for exponential retry when no <c>Retry-After</c> / reset
    /// hint is present. Attempt N waits <c>BaseBackoff * 2^(N-1)</c>.
    /// </summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on any single wait (including header-driven waits) to
    /// prevent pathological GitHub reset values from stalling the caller
    /// forever. A negative or zero value disables the cap.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum remaining quota below which <see cref="IGitHubRateLimitTracker.WaitIfNeededAsync(string, CancellationToken)"/>
    /// will delay until the window resets. Callers over this threshold proceed
    /// immediately.
    /// </summary>
    public int PreflightSafetyThreshold { get; set; } = 10;
}