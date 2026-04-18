// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// One hit in an <see cref="IExpertiseSearch"/> result set (#542). Pairs the
/// matched expertise entry with a ranking score so higher-priority matches
/// (exact slug, exact tag, aggregated-coverage) float to the top of the
/// page regardless of insertion order.
/// </summary>
/// <param name="Slug">
/// Directory-addressable slug for the capability (<c>expertise/{slug}</c>
/// when projected to the skill surface). Derived from
/// <see cref="ExpertiseDomain.Name"/> via the same slugification rules the
/// skill catalog uses, so the CLI can take a slug straight from a search
/// result and feed it into an <c>expertise/{slug}</c> skill invocation.
/// </param>
/// <param name="Domain">The matched expertise domain.</param>
/// <param name="Owner">
/// The directly contributing owner — agent for leaf-agent expertise, unit
/// for unit-projected expertise.
/// </param>
/// <param name="OwnerDisplayName">
/// Display name of the owning component from the directory entry; empty
/// string when the directory lookup failed.
/// </param>
/// <param name="AggregatingUnit">
/// When the entry surfaced via a unit's aggregated-coverage view, the
/// aggregating unit. <c>null</c> for agent-level / unit-own matches.
/// </param>
/// <param name="TypedContract">
/// <c>true</c> when <see cref="ExpertiseDomain.InputSchemaJson"/> is
/// non-null (i.e. skill-callable). The CLI and portal render this as a
/// distinct badge so operators can tell consultative-only entries from
/// typed ones.
/// </param>
/// <param name="Score">
/// Ranking score — higher is better. Ordering is:
/// exact slug match &gt; tag/domain/owner match &gt; text relevance &gt;
/// aggregated-coverage base.
/// </param>
/// <param name="MatchReason">
/// Short, human-readable explanation of the primary match reason
/// (e.g. <c>"exact slug"</c>, <c>"domain match"</c>, <c>"text match"</c>,
/// <c>"aggregated coverage"</c>). Surfaced so CLI operators and planners
/// can debug why a result was or wasn't returned.
/// </param>
public record ExpertiseSearchHit(
    string Slug,
    ExpertiseDomain Domain,
    Address Owner,
    string OwnerDisplayName,
    Address? AggregatingUnit,
    bool TypedContract,
    double Score,
    string MatchReason);

/// <summary>
/// Result page from <see cref="IExpertiseSearch.SearchAsync"/>. Carries
/// both the bounded page and the unbounded total so callers can render
/// pagination chrome without a second round-trip.
/// </summary>
/// <param name="Hits">The page of hits, sorted by score descending.</param>
/// <param name="TotalCount">
/// Total number of hits matching the query before pagination was applied.
/// Callers use this to compute "page N of M" or "X more results" chrome.
/// </param>
/// <param name="Limit">The effective page size applied to this call.</param>
/// <param name="Offset">The offset applied to this call.</param>
public record ExpertiseSearchResult(
    IReadOnlyList<ExpertiseSearchHit> Hits,
    int TotalCount,
    int Limit,
    int Offset);