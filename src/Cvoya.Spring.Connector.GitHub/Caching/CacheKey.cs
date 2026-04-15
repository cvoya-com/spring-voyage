// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Caching;

/// <summary>
/// Canonical identity for a cached GitHub response. Carries the logical
/// resource type (e.g. <c>pull_request</c>, <c>comments</c>) plus a
/// <see cref="Discriminator"/> that fully qualifies which instance of that
/// resource the entry represents (owner / repo / number / query-param hash).
/// Implementations use <see cref="ToString"/> as the storage-level identity;
/// two <see cref="CacheKey"/>s with equal <see cref="Resource"/> +
/// <see cref="Discriminator"/> must resolve to the same cached entry.
/// </summary>
/// <param name="Resource">Resource type, e.g. <c>pull_request</c>, <c>comments</c>, <c>review_threads</c>.</param>
/// <param name="Discriminator">Scope-qualifying portion (e.g. <c>owner/repo#42</c>, <c>owner/repo?state=open&amp;base=main</c>).</param>
/// <param name="Tags">
/// Tag names the entry should also be indexed under. Callers use
/// <see cref="CacheTags"/> to produce canonical tag strings so webhook-driven
/// invalidation and read paths agree on shape (e.g. <c>pr:owner/repo#42</c>).
/// </param>
public sealed record CacheKey(
    string Resource,
    string Discriminator,
    IReadOnlyList<string> Tags)
{
    /// <summary>
    /// Produces a canonical string form suitable for use as a dictionary key
    /// in the in-memory backend. Tags are intentionally excluded so two keys
    /// with the same logical identity but different tag sets collapse onto
    /// the same entry (last write wins for tag registration).
    /// </summary>
    public override string ToString() => $"{Resource}:{Discriminator}";

    /// <inheritdoc />
    public bool Equals(CacheKey? other) =>
        other is not null
        && Resource == other.Resource
        && Discriminator == other.Discriminator;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Resource, Discriminator);
}