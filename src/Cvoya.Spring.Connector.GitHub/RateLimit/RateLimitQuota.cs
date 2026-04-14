// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

/// <summary>
/// Snapshot of the GitHub rate-limit quota for a single resource bucket
/// (e.g. <c>core</c>, <c>search</c>, <c>graphql</c>).
/// </summary>
/// <param name="Resource">The rate-limit resource name as reported by GitHub in the
/// <c>x-ratelimit-resource</c> header.</param>
/// <param name="Limit">The total quota for the window.</param>
/// <param name="Remaining">The number of calls remaining in the current window.</param>
/// <param name="Reset">The UTC time at which the window resets and <paramref name="Remaining"/>
/// is restored to <paramref name="Limit"/>.</param>
/// <param name="ObservedAt">The time this snapshot was captured.</param>
public sealed record RateLimitQuota(
    string Resource,
    int Limit,
    int Remaining,
    DateTimeOffset Reset,
    DateTimeOffset ObservedAt);