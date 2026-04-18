// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExpertiseSearch"/> implementation (#542 Step 1). A
/// lexical / full-text search over every per-entity expertise declaration —
/// reads from <see cref="IDirectoryService.ListAllAsync"/> plus
/// <see cref="IExpertiseStore.GetDomainsAsync"/> (for own expertise) and
/// <see cref="IExpertiseAggregator.GetAsync(Address, BoundaryViewContext, CancellationToken)"/>
/// (to surface aggregated-coverage matches via unit projections).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in-memory.</b> The OSS default keeps the search path dependency-
/// free — no extra Postgres extensions, no separate index to rebuild. On
/// a 1000-entry tenant this is &lt;200ms end-to-end because:
/// (a) the directory entries are already cached in memory by
/// <see cref="DirectoryService"/>, (b) agent + unit own-expertise reads hit
/// the cached actor state, and (c) the aggregator result is cached per
/// unit address. The private cloud repo replaces this with a Postgres
/// full-text-search-backed store when tenant sizes grow. Issue #542
/// tracks that transition under Step 2.
/// </para>
/// <para>
/// <b>Ranking.</b> Per the issue brief:
/// <list type="number">
///   <item><description>Exact slug match (score 100).</description></item>
///   <item><description>Exact domain / tag match (score 80).</description></item>
///   <item><description>Owner address match — caller asked for a specific
///     contributor (score 70).</description></item>
///   <item><description>Text relevance — substring match on display name,
///     description, or domain name (score 30 + boost per match).</description></item>
///   <item><description>Aggregated-coverage match — the entry surfaced via a
///     descendant unit's projection (score 20 + any other matches on top).</description></item>
/// </list>
/// An entry with multiple match types gets the sum of every applicable
/// bonus so e.g. a typed-contract slug-exact hit outscores a consultative
/// substring hit.
/// </para>
/// <para>
/// <b>Boundary.</b> External callers see only unit-projected entries
/// (agent-level hits are hidden). Inside callers see the full scope.
/// Defence in depth: even if a misconfigured boundary leaked through the
/// aggregator, the search result set additionally filters agent-origin
/// hits for external callers.
/// </para>
/// </remarks>
public class InMemoryExpertiseSearch(
    IDirectoryService directoryService,
    IExpertiseStore expertiseStore,
    IExpertiseAggregator aggregator,
    ILoggerFactory loggerFactory) : IExpertiseSearch
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<InMemoryExpertiseSearch>();

    /// <inheritdoc />
    public async Task<ExpertiseSearchResult> SearchAsync(
        ExpertiseSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var context = query.Context ?? (query.Caller is null
            ? BoundaryViewContext.External
            : new BoundaryViewContext(Caller: query.Caller));

        // Clamp the page size and offset so a misconfigured caller cannot
        // ask for the entire catalog in one shot. See ExpertiseSearchQuery
        // for the contractual bounds.
        var effectiveLimit = query.Limit <= 0
            ? ExpertiseSearchQuery.DefaultLimit
            : Math.Min(query.Limit, ExpertiseSearchQuery.MaxLimit);
        var effectiveOffset = Math.Max(0, query.Offset);

        IReadOnlyList<DirectoryEntry> directory;
        try
        {
            directory = await directoryService.ListAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InMemoryExpertiseSearch: directory ListAll failed; returning empty.");
            return new ExpertiseSearchResult(Array.Empty<ExpertiseSearchHit>(), 0, effectiveLimit, effectiveOffset);
        }

        // Cache owner-display-name lookups so each candidate entry doesn't
        // re-resolve the same address when it surfaces through multiple
        // aggregating units.
        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in directory)
        {
            displayNames[AddressKey(entry.Address)] = entry.DisplayName;
        }

        // Collect candidates. We key by (slug, origin, aggregating unit) so
        // an entry contributed through multiple aggregation paths still shows
        // up once per surface rather than collapsing.
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        // Own-expertise pass. Every agent + unit contributes its own
        // expertise directly (not via the aggregator, because that would
        // lose the per-entity granularity external tag filters need).
        foreach (var entry in directory)
        {
            var scheme = entry.Address.Scheme;
            if (!string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<ExpertiseDomain> domains;
            try
            {
                domains = await expertiseStore.GetDomainsAsync(entry.Address, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "InMemoryExpertiseSearch: own-expertise read failed for {Scheme}://{Path}; skipping.",
                    entry.Address.Scheme, entry.Address.Path);
                continue;
            }

            foreach (var domain in domains)
            {
                var slug = ExpertiseSkillNaming.Slugify(domain.Name);
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                var key = SlugKey(slug, entry.Address, aggregating: null);
                if (!candidates.ContainsKey(key))
                {
                    candidates[key] = new Candidate(
                        slug,
                        domain,
                        entry.Address,
                        entry.DisplayName,
                        AggregatingUnit: null);
                }
            }
        }

        // Aggregated-coverage pass. For each unit, walk the boundary-aware
        // aggregate so entries that bubble up through a child unit surface
        // as "this unit covers X" hits — ranked just below direct matches.
        foreach (var entry in directory)
        {
            if (!string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AggregatedExpertise aggregated;
            try
            {
                aggregated = await aggregator.GetAsync(entry.Address, context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "InMemoryExpertiseSearch: aggregator read failed for unit {Path}; skipping aggregated coverage.",
                    entry.Address.Path);
                continue;
            }

            foreach (var agg in aggregated.Entries)
            {
                var slug = ExpertiseSkillNaming.Slugify(agg.Domain.Name);
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                // Skip rows whose origin is the same as the aggregating unit
                // (those are already present in the own-expertise pass).
                var sameAsOwn = AddressEquals(agg.Origin, entry.Address);
                if (sameAsOwn)
                {
                    continue;
                }

                // Defence in depth: external callers never see agent-origin
                // entries even if the aggregator returned them. An
                // inside-the-unit caller does.
                if (!context.Internal &&
                    !string.Equals(agg.Origin.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = SlugKey(slug, agg.Origin, aggregating: entry.Address);
                if (!candidates.ContainsKey(key))
                {
                    displayNames.TryGetValue(AddressKey(agg.Origin), out var ownerName);
                    candidates[key] = new Candidate(
                        slug,
                        agg.Domain,
                        agg.Origin,
                        ownerName ?? string.Empty,
                        AggregatingUnit: entry.Address);
                }
            }
        }

        // External callers hide agent-origin direct hits too. Inside callers
        // keep everything.
        var scored = new List<ExpertiseSearchHit>();
        var normalisedText = (query.Text ?? string.Empty).Trim();
        var haveText = normalisedText.Length > 0;
        var textLower = normalisedText.ToLowerInvariant();
        var domainFilter = query.Domains is { Count: > 0 }
            ? new HashSet<string>(query.Domains, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var candidate in candidates.Values)
        {
            // External boundary filter — no agent-origin direct hits.
            if (!context.Internal && candidate.AggregatingUnit is null &&
                !string.Equals(candidate.Owner.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Typed-contract filter.
            if (query.TypedOnly && candidate.Domain.InputSchemaJson is null)
            {
                continue;
            }

            // Owner filter.
            if (query.Owner is not null && !AddressEquals(candidate.Owner, query.Owner))
            {
                continue;
            }

            // Domain / tag filter.
            if (domainFilter is not null &&
                !domainFilter.Contains(candidate.Domain.Name) &&
                !domainFilter.Contains(candidate.Slug))
            {
                continue;
            }

            // Ranking.
            double score = 0;
            var reasons = new List<string>();

            if (haveText)
            {
                // Exact slug match dominates everything else.
                if (string.Equals(candidate.Slug, textLower, StringComparison.Ordinal) ||
                    string.Equals(candidate.Slug, ExpertiseSkillNaming.Slugify(normalisedText), StringComparison.Ordinal))
                {
                    score += 100;
                    reasons.Add("exact slug");
                }
                // Exact domain-name match.
                else if (string.Equals(candidate.Domain.Name, normalisedText, StringComparison.OrdinalIgnoreCase))
                {
                    score += 80;
                    reasons.Add("domain match");
                }
                else
                {
                    // Text relevance — substring matches across slug, display
                    // name, domain name, description, owner display name.
                    var textHits = 0;
                    if (candidate.Slug.Contains(textLower, StringComparison.Ordinal))
                    {
                        textHits++;
                    }
                    if (candidate.Domain.Name.Contains(textLower, StringComparison.OrdinalIgnoreCase))
                    {
                        textHits++;
                    }
                    if (!string.IsNullOrEmpty(candidate.Domain.Description) &&
                        candidate.Domain.Description.Contains(textLower, StringComparison.OrdinalIgnoreCase))
                    {
                        textHits++;
                    }
                    if (!string.IsNullOrEmpty(candidate.OwnerDisplayName) &&
                        candidate.OwnerDisplayName.Contains(textLower, StringComparison.OrdinalIgnoreCase))
                    {
                        textHits++;
                    }

                    if (textHits > 0)
                    {
                        score += 30 + textHits * 5;
                        reasons.Add("text match");
                    }
                    else
                    {
                        // No text hit, and the caller asked for one. Skip.
                        continue;
                    }
                }
            }
            else
            {
                // No free-text → every remaining entry is eligible with a
                // baseline score so pagination is deterministic.
                score += 10;
                reasons.Add("no text");
            }

            // Bonus: owner filter match.
            if (query.Owner is not null)
            {
                score += 5;
            }

            // Bonus: typed contract (we nudge typed entries above
            // consultative ones at equal relevance because the issue calls
            // out "typed-contract skill-callable" as a primary use case).
            if (candidate.Domain.InputSchemaJson is not null)
            {
                score += 3;
            }

            // Aggregated-coverage base penalty so direct hits outrank
            // reachable-via-a-child hits at the same text relevance.
            if (candidate.AggregatingUnit is not null)
            {
                score = Math.Max(0, score - 10);
                if (!reasons.Contains("text match", StringComparer.Ordinal) &&
                    !reasons.Contains("exact slug", StringComparer.Ordinal) &&
                    !reasons.Contains("domain match", StringComparer.Ordinal))
                {
                    reasons.Add("aggregated coverage");
                }
            }

            scored.Add(new ExpertiseSearchHit(
                Slug: candidate.Slug,
                Domain: candidate.Domain,
                Owner: candidate.Owner,
                OwnerDisplayName: candidate.OwnerDisplayName,
                AggregatingUnit: candidate.AggregatingUnit,
                TypedContract: candidate.Domain.InputSchemaJson is not null,
                Score: score,
                MatchReason: string.Join(" + ", reasons)));
        }

        // Deterministic tiebreak: score DESC, then slug ASC, then owner ASC
        // so repeat runs produce identical pages.
        scored.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0)
            {
                return c;
            }
            c = string.CompareOrdinal(a.Slug, b.Slug);
            if (c != 0)
            {
                return c;
            }
            return string.CompareOrdinal(
                a.Owner.Scheme + "://" + a.Owner.Path,
                b.Owner.Scheme + "://" + b.Owner.Path);
        });

        var total = scored.Count;
        var page = scored
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToList();

        return new ExpertiseSearchResult(page, total, effectiveLimit, effectiveOffset);
    }

    private static string AddressKey(Address address) =>
        address.Scheme + "://" + address.Path;

    private static bool AddressEquals(Address a, Address b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Path, b.Path, StringComparison.Ordinal);

    private static string SlugKey(string slug, Address owner, Address? aggregating) =>
        slug + "|" + AddressKey(owner) + "|" + (aggregating is null ? string.Empty : AddressKey(aggregating));

    private sealed record Candidate(
        string Slug,
        ExpertiseDomain Domain,
        Address Owner,
        string OwnerDisplayName,
        Address? AggregatingUnit);
}