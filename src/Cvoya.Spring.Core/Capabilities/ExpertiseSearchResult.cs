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
/// <param name="AncestorChain">
/// Ordered chain of aggregating units from the direct owner up to the
/// highest projecting ancestor (#553). Empty for direct hits — populated
/// only when the entry surfaced via an aggregated-coverage projection,
/// in which case the list is the aggregator's walk minus the origin
/// itself. Callers can render the chain as a breadcrumb so an operator
/// can see the full projection lineage rather than only the immediate
/// <see cref="AggregatingUnit"/>.
/// </param>
/// <param name="ProjectionPaths">
/// Set of <c>projection/{slug}</c> paths through which this entry
/// surfaces in the caller's boundary (#553). Today we emit one path per
/// aggregating ancestor (shape <c>projection/{slug}</c> rooted at the
/// slug — the per-ancestor projection identity — so downstream
/// renderers can count "this capability is reachable via N projections"
/// without re-walking the chain). Empty for direct hits.
/// </param>
public record ExpertiseSearchHit(
    string Slug,
    ExpertiseDomain Domain,
    Address Owner,
    string OwnerDisplayName,
    Address? AggregatingUnit,
    bool TypedContract,
    double Score,
    string MatchReason,
    IReadOnlyList<Address>? AncestorChain = null,
    IReadOnlyList<string>? ProjectionPaths = null);

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