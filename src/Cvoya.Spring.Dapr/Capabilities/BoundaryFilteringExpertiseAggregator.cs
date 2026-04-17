// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Decorator that layers a <see cref="UnitBoundary"/> over the raw output of
/// an inner <see cref="IExpertiseAggregator"/> (#413). The inner aggregator
/// stays responsible for the recursive walk and cache; this decorator only
/// rewrites the outside-the-unit view by applying opacity, projection, and
/// synthesis rules to the base <see cref="AggregatedExpertise.Entries"/>
/// list.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the default <see cref="IExpertiseAggregator"/> by
/// <c>AddCvoyaSpringDapr</c>, wrapping <see cref="ExpertiseAggregator"/>.
/// Call sites that bind the interface get boundary-aware behaviour for free;
/// tests that want the raw aggregator can resolve <see cref="ExpertiseAggregator"/>
/// directly, which is registered as a concrete singleton.
/// </para>
/// <para>
/// Rule semantics:
/// <list type="bullet">
///   <item><description><b>Opacity</b> — every entry whose <c>domain</c>
///     and <c>origin</c> match an opacity rule is removed from the view.
///     Multiple rules OR together. Opacity takes precedence over projection
///     and synthesis — a matched opaque entry is gone, not rewritten.</description></item>
///   <item><description><b>Projection</b> — the first matching projection
///     rule rewrites the entry's domain name, level, and description. Origin
///     and path are preserved so <see cref="ExpertiseEntry.Origin"/> still
///     points at the true contributor — downstream permission checks
///     (<see href="https://github.com/cvoya-com/spring-voyage/issues/414">#414</see>)
///     continue to work over the projected view.</description></item>
///   <item><description><b>Synthesis</b> — contributing entries are dropped
///     and replaced with a single synthesised entry attributed to the
///     aggregating unit (<c>origin = unit</c>, <c>path = [unit]</c>).
///     Only entries not already opaque-stripped are candidates.</description></item>
/// </list>
/// </para>
/// <para>
/// Matching patterns are case-insensitive and support a trailing
/// <c>*</c> wildcard (prefix match). A <c>null</c> pattern matches anything
/// along that dimension.
/// </para>
/// </remarks>
public class BoundaryFilteringExpertiseAggregator(
    ExpertiseAggregator inner,
    IUnitBoundaryStore boundaryStore,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IExpertiseAggregator
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<BoundaryFilteringExpertiseAggregator>();

    /// <inheritdoc />
    public Task<AggregatedExpertise> GetAsync(Address unit, CancellationToken cancellationToken = default)
        => inner.GetAsync(unit, cancellationToken);

    /// <inheritdoc />
    public async Task<AggregatedExpertise> GetAsync(
        Address unit,
        BoundaryViewContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(context);

        var raw = await inner.GetAsync(unit, cancellationToken);

        // Internal callers bypass the boundary — they get the raw recursive
        // view. This includes the unit itself and its own members. Outside
        // callers get the boundary-applied view.
        if (context.Internal)
        {
            return raw;
        }

        UnitBoundary boundary;
        try
        {
            boundary = await boundaryStore.GetAsync(unit, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never fail the aggregate read on a boundary-store error —
            // degrade to "no rules" (i.e. transparent). The warning tells
            // operators the configured boundary is not being enforced.
            _logger.LogWarning(ex,
                "Boundary read failed for {Scheme}://{Path}; returning transparent view.",
                unit.Scheme, unit.Path);
            return raw;
        }

        if (boundary is null || boundary.IsEmpty)
        {
            return raw;
        }

        var filtered = Apply(unit, raw.Entries, boundary);
        return new AggregatedExpertise(raw.Unit, filtered, raw.Depth, timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public Task InvalidateAsync(Address origin, CancellationToken cancellationToken = default)
        => inner.InvalidateAsync(origin, cancellationToken);

    /// <summary>
    /// Applies the configured rules to the raw entry list and returns the
    /// outside-the-unit view.
    /// </summary>
    internal static IReadOnlyList<ExpertiseEntry> Apply(
        Address unit,
        IReadOnlyList<ExpertiseEntry> rawEntries,
        UnitBoundary boundary)
    {
        // Step 1: drop opaque entries.
        var opacities = boundary.Opacities ?? Array.Empty<BoundaryOpacityRule>();
        var visible = new List<ExpertiseEntry>(rawEntries.Count);
        foreach (var entry in rawEntries)
        {
            var opaque = false;
            foreach (var rule in opacities)
            {
                if (MatchDomain(rule.DomainPattern, entry.Domain.Name)
                    && MatchOrigin(rule.OriginPattern, entry.Origin))
                {
                    opaque = true;
                    break;
                }
            }
            if (!opaque)
            {
                visible.Add(entry);
            }
        }

        // Step 2: collect synthesised entries and remove their contributors.
        var syntheses = boundary.Syntheses ?? Array.Empty<BoundarySynthesisRule>();
        var consumedBySynthesis = new HashSet<int>();
        var synthesised = new List<ExpertiseEntry>();
        foreach (var rule in syntheses)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                continue;
            }

            ExpertiseLevel? strongest = null;
            var matchedAny = false;
            for (var i = 0; i < visible.Count; i++)
            {
                if (consumedBySynthesis.Contains(i))
                {
                    continue;
                }
                var entry = visible[i];
                if (MatchDomain(rule.DomainPattern, entry.Domain.Name)
                    && MatchOrigin(rule.OriginPattern, entry.Origin))
                {
                    matchedAny = true;
                    consumedBySynthesis.Add(i);
                    if (entry.Domain.Level is { } level
                        && (strongest is null || (int)level > (int)strongest))
                    {
                        strongest = level;
                    }
                }
            }

            if (!matchedAny)
            {
                // Don't fabricate a team capability nobody contributes to.
                continue;
            }

            var chosenLevel = rule.Level ?? strongest;
            synthesised.Add(new ExpertiseEntry(
                new ExpertiseDomain(rule.Name, rule.Description ?? string.Empty, chosenLevel),
                unit,
                new[] { unit }));
        }

        // Step 3: apply projections to the remaining raw entries (entries
        // consumed by synthesis are already gone). Projections are first-
        // match-wins in declaration order.
        var projections = boundary.Projections ?? Array.Empty<BoundaryProjectionRule>();
        var projected = new List<ExpertiseEntry>(visible.Count);
        for (var i = 0; i < visible.Count; i++)
        {
            if (consumedBySynthesis.Contains(i))
            {
                continue;
            }

            var entry = visible[i];
            var rewrittenDomain = entry.Domain;
            foreach (var rule in projections)
            {
                if (MatchDomain(rule.DomainPattern, entry.Domain.Name)
                    && MatchOrigin(rule.OriginPattern, entry.Origin))
                {
                    rewrittenDomain = new ExpertiseDomain(
                        string.IsNullOrEmpty(rule.RenameTo) ? entry.Domain.Name : rule.RenameTo,
                        rule.Retag ?? entry.Domain.Description,
                        rule.OverrideLevel ?? entry.Domain.Level);
                    break;
                }
            }

            projected.Add(entry with { Domain = rewrittenDomain });
        }

        // Step 4: combine the projected-raw and synthesised sets, sorted
        // stably by domain name so callers get a deterministic wire.
        projected.AddRange(synthesised);
        projected.Sort((a, b) =>
        {
            var byName = string.CompareOrdinal(a.Domain.Name, b.Domain.Name);
            return byName != 0
                ? byName
                : string.CompareOrdinal(a.Origin.Path, b.Origin.Path);
        });
        return projected;
    }

    internal static bool MatchDomain(string? pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }
        return MatchPattern(pattern, value ?? string.Empty);
    }

    internal static bool MatchOrigin(string? pattern, Address origin)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }
        var full = $"{origin.Scheme}://{origin.Path}";
        return MatchPattern(pattern, full);
    }

    private static bool MatchPattern(string pattern, string value)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }
}