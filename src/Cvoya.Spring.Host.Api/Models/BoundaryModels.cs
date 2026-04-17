// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Wire shape for a single opacity rule on a unit's boundary (#413). An
/// opaque entry is stripped from the outside-the-unit view.
/// </summary>
/// <param name="DomainPattern">
/// Case-insensitive exact-match or <c>*</c>-wildcard pattern on the
/// aggregated entry's domain name. <c>null</c> means "match any domain".
/// </param>
/// <param name="OriginPattern">
/// Optional <c>scheme://path</c> pattern (supports trailing <c>*</c>). Matched
/// against the entry's origin address. <c>null</c> means "match any origin".
/// </param>
public record BoundaryOpacityRuleDto(
    string? DomainPattern,
    string? OriginPattern);

/// <summary>
/// Wire shape for a single projection rule on a unit's boundary. Rewrites
/// matching entries for outside callers rather than hiding them.
/// </summary>
/// <param name="DomainPattern">Same semantics as on opacity.</param>
/// <param name="OriginPattern">Same semantics as on opacity.</param>
/// <param name="RenameTo">Optional replacement for the domain name.</param>
/// <param name="Retag">Optional replacement for the domain description.</param>
/// <param name="OverrideLevel">
/// Optional replacement for the domain level. One of
/// <c>beginner | intermediate | advanced | expert</c> (case-insensitive).
/// </param>
public record BoundaryProjectionRuleDto(
    string? DomainPattern,
    string? OriginPattern,
    string? RenameTo,
    string? Retag,
    string? OverrideLevel);

/// <summary>
/// Wire shape for a single synthesis rule on a unit's boundary. Collapses
/// matching entries into a single synthesised entry attributed to the unit.
/// </summary>
/// <param name="Name">Name of the synthesised domain.</param>
/// <param name="DomainPattern">Same semantics as on opacity.</param>
/// <param name="OriginPattern">Same semantics as on opacity.</param>
/// <param name="Description">Optional description for the synthesised domain.</param>
/// <param name="Level">
/// Optional explicit level. When <c>null</c> the server uses the strongest
/// level seen among matched entries.
/// </param>
public record BoundarySynthesisRuleDto(
    string Name,
    string? DomainPattern,
    string? OriginPattern,
    string? Description,
    string? Level);

/// <summary>
/// Wire shape for the unit boundary (#413). All three slots are optional;
/// a boundary with no rules is equivalent to "transparent".
/// </summary>
/// <param name="Opacities">Opacity rules that hide matching entries.</param>
/// <param name="Projections">Projection rules that rewrite matching entries.</param>
/// <param name="Syntheses">Synthesis rules that collapse matches into a derived entry.</param>
public record UnitBoundaryResponse(
    IReadOnlyList<BoundaryOpacityRuleDto>? Opacities = null,
    IReadOnlyList<BoundaryProjectionRuleDto>? Projections = null,
    IReadOnlyList<BoundarySynthesisRuleDto>? Syntheses = null)
{
    /// <summary>
    /// Projects a core <see cref="UnitBoundary"/> to the wire shape.
    /// </summary>
    public static UnitBoundaryResponse From(UnitBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        return new UnitBoundaryResponse(
            boundary.Opacities?
                .Select(r => new BoundaryOpacityRuleDto(r.DomainPattern, r.OriginPattern))
                .ToList(),
            boundary.Projections?
                .Select(r => new BoundaryProjectionRuleDto(
                    r.DomainPattern, r.OriginPattern, r.RenameTo, r.Retag,
                    r.OverrideLevel?.ToString().ToLowerInvariant()))
                .ToList(),
            boundary.Syntheses?
                .Select(r => new BoundarySynthesisRuleDto(
                    r.Name, r.DomainPattern, r.OriginPattern, r.Description,
                    r.Level?.ToString().ToLowerInvariant()))
                .ToList());
    }

    /// <summary>
    /// Projects the wire shape back to a core <see cref="UnitBoundary"/>.
    /// Unknown level strings resolve to <c>null</c> so a client that omits
    /// a level never fails deserialisation.
    /// </summary>
    public UnitBoundary ToCore()
    {
        return new UnitBoundary(
            Opacities?
                .Select(r => new BoundaryOpacityRule(r.DomainPattern, r.OriginPattern))
                .ToList(),
            Projections?
                .Select(r => new BoundaryProjectionRule(
                    r.DomainPattern, r.OriginPattern, r.RenameTo, r.Retag, ParseLevel(r.OverrideLevel)))
                .ToList(),
            Syntheses?
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new BoundarySynthesisRule(
                    r.Name, r.DomainPattern, r.OriginPattern, r.Description, ParseLevel(r.Level)))
                .ToList());
    }

    private static ExpertiseLevel? ParseLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Enum.TryParse<ExpertiseLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}