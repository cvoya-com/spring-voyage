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

        /// <summary>The <c>github_get_project_v2</c> skill result (GraphQL).</summary>
        public const string ProjectV2 = "project_v2";

        /// <summary>The <c>github_get_project_v2_item</c> skill result (GraphQL).</summary>
        public const string ProjectV2Item = "project_v2_item";

        /// <summary>
        /// The <c>github_list_projects_v2</c> and <c>github_list_project_v2_items</c>
        /// skill results (GraphQL). A single list-oriented TTL bucket captures both
        /// the org-wide project list and individual project item-page slices —
        /// callers that need different lifetimes can still split via
        /// <see cref="GitHubResponseCacheOptions.Ttls"/>, but defaults share one knob.
        /// </summary>
        public const string ProjectV2List = "project_v2_list";
    }

    /// <summary>
    /// Default TTL applied specifically to Projects v2 resource reads when
    /// nothing is configured in <see cref="Ttls"/>. Projects v2 triage views
    /// refresh aggressively (column drag, iteration advance, status edits),
    /// so the read-side cache needs a noticeably shorter default than the
    /// global <see cref="DefaultTtl"/>. Public so tests and integrators can
    /// reason about the effective lifetime without reading into the
    /// constructor.
    /// </summary>
    public static readonly TimeSpan DefaultProjectV2Ttl = TimeSpan.FromSeconds(30);

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
    /// values fall back to <see cref="DefaultTtl"/>. Projects v2 resources
    /// are pre-seeded with <see cref="DefaultProjectV2Ttl"/> so they default
    /// to 30s even without explicit configuration; anything present in
    /// <c>GitHub:ResponseCache:Ttls</c> overrides the seed value on bind.
    /// </summary>
    public Dictionary<string, TimeSpan> Ttls { get; set; } = new(StringComparer.Ordinal)
    {
        [Resources.ProjectV2] = DefaultProjectV2Ttl,
        [Resources.ProjectV2Item] = DefaultProjectV2Ttl,
        [Resources.ProjectV2List] = DefaultProjectV2Ttl,
    };

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
