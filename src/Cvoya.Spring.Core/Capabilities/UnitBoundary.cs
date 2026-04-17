// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using System.Runtime.Serialization;

/// <summary>
/// Declarative boundary configuration for a unit (#413). A unit's boundary
/// controls what members of the unit expose to callers outside the unit —
/// which entries are <em>opaque</em> (hidden), which are <em>projected</em>
/// (visible with optional rename/retag), and which are <em>synthesised</em>
/// (a derived aggregate shown instead of the raw members).
/// </summary>
/// <remarks>
/// <para>
/// The boundary layers on top of <see cref="IExpertiseAggregator"/>: the base
/// aggregator still produces the transparent, recursive view over the member
/// graph (see ADR 0006). Boundary rules are applied after aggregation, so
/// <see cref="ExpertiseEntry.Origin"/> and <see cref="ExpertiseEntry.Path"/>
/// are available to match rules against.
/// </para>
/// <para>
/// Boundary enforcement is <em>caller-aware</em>: a caller that is inside
/// the unit (the unit itself, or a nested sub-unit) sees the raw aggregate;
/// an outside caller sees the projected / synthesised view with opaque
/// entries stripped. The caller identity is supplied via
/// <see cref="BoundaryViewContext"/>.
/// </para>
/// <para>
/// Every slot is optional — a <see cref="UnitBoundary"/> with no rules is
/// equivalent to "transparent" and the decorator is a straight pass-through.
/// </para>
/// <para>
/// Carried across the Dapr Actor remoting boundary as the argument to
/// <c>IUnitActor.GetBoundaryAsync</c> / <c>SetBoundaryAsync</c>, so the
/// positional record is opted into <c>DataContractSerializer</c> (#319).
/// </para>
/// </remarks>
/// <param name="Opacities">
/// Rules that hide matching <see cref="ExpertiseEntry"/> values from outside
/// callers. An entry is opaque when at least one rule matches.
/// </param>
/// <param name="Projections">
/// Rules that rewrite matching entries for outside callers — rename the
/// domain, override the level, or replace the description.
/// </param>
/// <param name="Syntheses">
/// Rules that replace a set of matching entries with a single synthesised
/// entry attributed to this unit. The raw entries that contributed are
/// dropped from the outside view; the synthesised entry is emitted in their
/// place with <see cref="ExpertiseEntry.Origin"/> pointing at the unit.
/// </param>
[DataContract]
public record UnitBoundary(
    [property: DataMember(Order = 0)] IReadOnlyList<BoundaryOpacityRule>? Opacities = null,
    [property: DataMember(Order = 1)] IReadOnlyList<BoundaryProjectionRule>? Projections = null,
    [property: DataMember(Order = 2)] IReadOnlyList<BoundarySynthesisRule>? Syntheses = null)
{
    /// <summary>
    /// Returns an empty boundary — no rules in any dimension. Equivalent to
    /// "transparent": outside callers see exactly the aggregator's raw view.
    /// </summary>
    public static UnitBoundary Empty { get; } = new();

    /// <summary>
    /// Returns <c>true</c> when every dimension is <c>null</c> or empty — the
    /// boundary imposes no constraints. Repositories and actors may treat an
    /// empty boundary as "no row persisted".
    /// </summary>
    public bool IsEmpty =>
        (Opacities is null || Opacities.Count == 0)
        && (Projections is null || Projections.Count == 0)
        && (Syntheses is null || Syntheses.Count == 0);
}

/// <summary>
/// Declares an opacity rule — a matcher that marks every aggregated
/// <see cref="ExpertiseEntry"/> it accepts as hidden from outside callers.
/// Opacity is a <em>deny</em> filter: a matching entry is stripped from the
/// outside view. Multiple rules OR together.
/// </summary>
/// <remarks>
/// Opacity and projection are distinct: an opaque entry is removed, while
/// a projected entry is rewritten and still shown. If an entry matches both
/// an opacity rule and a projection rule, opacity wins — the entry is
/// removed.
/// </remarks>
/// <param name="DomainPattern">
/// Case-insensitive exact-match or <c>*</c>-wildcard pattern on the
/// <c>ExpertiseDomain.Name</c>. <c>null</c> means "match any domain".
/// </param>
/// <param name="OriginPattern">
/// Optional scheme-and-path pattern (e.g. <c>agent://internal-*</c>) that
/// matches against the entry's origin. <c>null</c> means "match any origin".
/// </param>
[DataContract]
public record BoundaryOpacityRule(
    [property: DataMember(Order = 0)] string? DomainPattern = null,
    [property: DataMember(Order = 1)] string? OriginPattern = null);

/// <summary>
/// Declares a projection rule — a matcher plus a rewrite. Every matching
/// entry is replaced in the outside view with a rewritten copy. Multiple
/// projection rules are applied in declaration order; the first matching
/// rule wins.
/// </summary>
/// <param name="DomainPattern">
/// Same matcher semantics as <see cref="BoundaryOpacityRule.DomainPattern"/>.
/// </param>
/// <param name="OriginPattern">
/// Same matcher semantics as <see cref="BoundaryOpacityRule.OriginPattern"/>.
/// </param>
/// <param name="RenameTo">
/// Optional replacement for the domain <c>Name</c>. <c>null</c> leaves the
/// original name.
/// </param>
/// <param name="Retag">
/// Optional replacement for the domain <c>Description</c>. <c>null</c> leaves
/// the original description.
/// </param>
/// <param name="OverrideLevel">
/// Optional replacement for the domain <c>Level</c>. <c>null</c> leaves the
/// original level (which itself may be <c>null</c>).
/// </param>
[DataContract]
public record BoundaryProjectionRule(
    [property: DataMember(Order = 0)] string? DomainPattern = null,
    [property: DataMember(Order = 1)] string? OriginPattern = null,
    [property: DataMember(Order = 2)] string? RenameTo = null,
    [property: DataMember(Order = 3)] string? Retag = null,
    [property: DataMember(Order = 4)] ExpertiseLevel? OverrideLevel = null);

/// <summary>
/// Declares a synthesis rule — a matcher plus a synthesised replacement.
/// Every matching entry is removed from the outside view; in their place a
/// single synthesised entry (attributed to this unit) is emitted. Used to
/// advertise "team-level React expertise" instead of eight individual React
/// entries from individual members.
/// </summary>
/// <remarks>
/// When nothing matches, the synthesis contributes nothing — the rule
/// never fabricates an empty team capability. The level defaults to the
/// strongest <see cref="ExpertiseLevel"/> seen among matched entries when
/// <paramref name="Level"/> is <c>null</c>.
/// </remarks>
/// <param name="Name">Name of the synthesised domain (required).</param>
/// <param name="DomainPattern">
/// Pattern matched against contributing entries' domain names. <c>null</c>
/// matches any domain — useful to synthesize a generic team capability from
/// every member.
/// </param>
/// <param name="OriginPattern">
/// Optional origin pattern — synthesize only from entries whose origin
/// matches. <c>null</c> matches any origin.
/// </param>
/// <param name="Description">
/// Optional description attached to the synthesised domain.
/// </param>
/// <param name="Level">
/// Optional explicit level for the synthesised capability. When <c>null</c>
/// the decorator uses the strongest level observed across matched entries.
/// </param>
[DataContract]
public record BoundarySynthesisRule(
    [property: DataMember(Order = 0)] string Name,
    [property: DataMember(Order = 1)] string? DomainPattern = null,
    [property: DataMember(Order = 2)] string? OriginPattern = null,
    [property: DataMember(Order = 3)] string? Description = null,
    [property: DataMember(Order = 4)] ExpertiseLevel? Level = null);