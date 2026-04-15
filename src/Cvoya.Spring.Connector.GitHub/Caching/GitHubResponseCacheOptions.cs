// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// Tuning options for the GitHub response cache. Bound from the
/// <c>GitHub:ResponseCache</c> configuration section by
/// <c>ServiceCollectionExtensions.AddCvoyaSpringConnectorGitHub</c>.
/// </summary>
public class GitHubResponseCacheOptions
{
    /// <summary>
    /// Well-known resource type names used for <see cref="Ttls"/> lookup and
    /// as the <see cref="CacheKey.Resource"/> value on reads. Kept as
    /// constants so the read side and configuration side can't silently drift.
    /// </summary>
    public static class Resources
    {
        /// <summary>The <c>github_get_pull_request</c> skill result.</summary>
        public const string PullRequest = "pull_request";

        /// <summary>The <c>github_list_pull_requests</c> skill result.</summary>
        public const string PullRequestList = "pull_request_list";

        /// <summary>The <c>github_list_comments</c> skill result.</summary>
        public const string Comments = "comments";

        /// <summary>The <c>github_list_pull_request_reviews</c> skill result.</summary>
        public const string PullRequestReviews = "pull_request_reviews";

        /// <summary>The <c>github_list_review_threads</c> skill result (GraphQL).</summary>
        public const string ReviewThreads = "review_threads";
    }

    /// <summary>
    /// Gets or sets whether the response cache is active. When <c>false</c>
    /// the cache is a no-op pass-through — useful for debugging or for
    /// deployments that already layer their own caching underneath.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the default TTL applied to any resource not explicitly
    /// listed in <see cref="Ttls"/>. 60s matches the issue-comment-refresh
    /// cadence agents tend to use when polling a PR for feedback without
    /// being so long that edits linger visibly past a full webhook lag.
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets per-resource TTL overrides. Keyed by the resource
    /// identifier (see <see cref="Resources"/>). Missing / zero / negative
    /// values fall back to <see cref="DefaultTtl"/>.
    /// </summary>
    public Dictionary<string, TimeSpan> Ttls { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets how often the in-memory backend sweeps for expired
    /// entries. Purely a memory-pressure knob — reads already skip expired
    /// entries regardless of the sweep cadence.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the effective TTL for <paramref name="resource"/>, falling
    /// back to <see cref="DefaultTtl"/> when no override is configured.
    /// </summary>
    public TimeSpan ResolveTtl(string resource)
    {
        if (Ttls.TryGetValue(resource, out var ttl) && ttl > TimeSpan.Zero)
        {
            return ttl;
        }
        return DefaultTtl;
    }
}