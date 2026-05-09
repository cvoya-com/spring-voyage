// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Tuning knobs for <see cref="InstallationTokenCache"/>. Bound from the
/// <c>GitHub:TokenCache</c> configuration section by
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringConnectorGitHub"/>.
/// </summary>
public class InstallationTokenCacheOptions
{
    /// <summary>
    /// How close to a token's GitHub-issued <c>expires_at</c> the cache is
    /// willing to return it. When the remaining TTL drops below this window,
    /// the next call re-mints pre-emptively so callers never hit the cliff.
    /// Defaults to 60 seconds, matching the issue spec.
    /// </summary>
    public TimeSpan ProactiveRefreshWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Upper bound on the cached TTL regardless of what GitHub returns. Acts
    /// as a safety ceiling if GitHub ever hands out an unusually long token
    /// (today they are always one hour). Set to <see cref="TimeSpan.Zero"/>
    /// to disable the ceiling.
    /// </summary>
    public TimeSpan CeilingTtl { get; set; } = TimeSpan.FromHours(1);
}
