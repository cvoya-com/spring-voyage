// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

/// <summary>
/// Persistable snapshot of the GitHub rate-limit quota for a single
/// resource bucket (e.g. <c>core</c>, <c>search</c>, <c>graphql</c>). This
/// is the wire / storage-layer type used by <see cref="IRateLimitStateStore"/>
/// — deliberately narrower than the in-process
/// <see cref="RateLimitQuota"/> record so the store does not need to
/// understand the <c>Resource</c> name (which is already encoded in the
/// state-store key).
/// </summary>
/// <param name="Remaining">Number of calls remaining in the current window.</param>
/// <param name="Limit">Total quota for the window.</param>
/// <param name="ResetAt">UTC instant at which the window resets.</param>
/// <param name="UpdatedAt">UTC instant at which this snapshot was captured.</param>
public sealed record RateLimitSnapshot(
    int Remaining,
    int Limit,
    DateTimeOffset ResetAt,
    DateTimeOffset UpdatedAt);