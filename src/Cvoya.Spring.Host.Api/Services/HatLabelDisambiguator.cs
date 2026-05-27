// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Computes the server-side <c>disambiguatedLabel</c> for a Hat (Human)
/// rendered alongside same-named siblings in a result-set scope
/// (ADR-0062 § 5 / #2829). Centralises the priority rule so the portal
/// (<c>HatChip</c>, <c>HumanFromSelector</c>, <c>YourHatsPanel</c>,
/// inbox-toolbar filter chip) and the CLI (<c>RefResolver</c> ambiguity
/// prompt) all render the same string.
/// </summary>
/// <remarks>
/// <para>
/// Rule, in priority order:
/// </para>
/// <list type="number">
/// <item><description>
/// Start with the Hat's display name (falling back to <c>username</c>
/// when display name is null/whitespace, as the existing endpoints
/// already do).
/// </description></item>
/// <item><description>
/// If no other Hat in the context shares the same name → use as-is.
/// </description></item>
/// <item><description>
/// If collisions exist and the colliding Hats differ on
/// <em>role</em> → append the role: <c>"Bob — designer"</c> vs
/// <c>"Bob — reviewer"</c>.
/// </description></item>
/// <item><description>
/// If still colliding and they differ on <em>unit</em> → append the
/// unit: <c>"Bob (Magazine)"</c> vs <c>"Bob (Newsletter)"</c>.
/// </description></item>
/// <item><description>
/// If still colliding → append a short Guid prefix (first 4 hex chars
/// of <c>humans.id</c>, no-dash): <c>"Bob #12ab"</c> vs <c>"Bob #34cd"</c>.
/// Always disambiguates.
/// </description></item>
/// </list>
/// <para>
/// The "context" is the result-set scope, not the global Hat set: a
/// page of 5 inbox rows disambiguates within those 5, even if the
/// tenant has dozens of same-named Hats outside the page.
/// </para>
/// </remarks>
public static class HatLabelDisambiguator
{
    /// <summary>
    /// Resolves the disambiguated label for one Hat in a context-scoped
    /// candidate set. Pass the full candidate set as <paramref name="context"/>;
    /// the target Hat must appear in it (matched by <see cref="HatLabelCandidate.HumanId"/>).
    /// </summary>
    /// <param name="target">The Hat to render the label for.</param>
    /// <param name="context">
    /// The full result-set scope. Same-name siblings drive the
    /// disambiguation tier; absent siblings → returns the raw base name.
    /// </param>
    public static string Disambiguate(
        HatLabelCandidate target,
        IReadOnlyList<HatLabelCandidate> context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var baseName = NormaliseName(target.BaseName);

        // Find every sibling sharing the same base name (case-insensitive,
        // matching the CLI's exact-match resolution rule) excluding the
        // target itself.
        var siblings = context
            .Where(c =>
                c.HumanId != target.HumanId
                && string.Equals(NormaliseName(c.BaseName), baseName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (siblings.Count == 0)
        {
            return baseName;
        }

        // Tier 2: append role. We pick the *target's* first role; the
        // tier succeeds when at least one sibling's roles differ.
        var targetRole = FirstNonEmpty(target.Roles);
        if (!string.IsNullOrWhiteSpace(targetRole))
        {
            var anySiblingDiffersOnRole = siblings.Any(s =>
                !string.Equals(
                    FirstNonEmpty(s.Roles) ?? string.Empty,
                    targetRole,
                    StringComparison.OrdinalIgnoreCase));
            if (anySiblingDiffersOnRole)
            {
                return $"{baseName} — {targetRole}";
            }
        }

        // Tier 3: append unit. Same predicate, on the per-Hat unit name.
        var targetUnit = NormaliseName(target.UnitDisplayName);
        if (!string.IsNullOrWhiteSpace(targetUnit))
        {
            var anySiblingDiffersOnUnit = siblings.Any(s =>
                !string.Equals(
                    NormaliseName(s.UnitDisplayName),
                    targetUnit,
                    StringComparison.OrdinalIgnoreCase));
            if (anySiblingDiffersOnUnit)
            {
                return $"{baseName} ({targetUnit})";
            }
        }

        // Tier 4: Guid suffix. Always disambiguates — every Hat has a
        // distinct id. Rendered no-dash to match the wire form on
        // canonical addresses.
        var suffix = target.HumanId.ToString("N").AsSpan(0, 4).ToString();
        return $"{baseName} #{suffix}";
    }

    /// <summary>
    /// Batch helper: disambiguates every Hat in the supplied set against
    /// the same set. Returns a dictionary keyed by <c>HumanId</c> so the
    /// caller can stamp the label onto each row without a quadratic
    /// scan.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> DisambiguateAll(
        IReadOnlyList<HatLabelCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var result = new Dictionary<Guid, string>(candidates.Count);
        foreach (var c in candidates)
        {
            result[c.HumanId] = Disambiguate(c, candidates);
        }
        return result;
    }

    private static string NormaliseName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? FirstNonEmpty(IReadOnlyList<string>? roles)
    {
        if (roles is null)
        {
            return null;
        }
        foreach (var r in roles)
        {
            if (!string.IsNullOrWhiteSpace(r))
            {
                return r.Trim();
            }
        }
        return null;
    }
}

/// <summary>
/// One candidate row in a <see cref="HatLabelDisambiguator"/> scope.
/// Carries the minimum fields the disambiguation rule needs: stable id
/// (for the Guid-suffix tier), the base display name, the Hat's first
/// unit-membership (role + unit display name). A Hat with no
/// memberships passes both <see cref="UnitDisplayName"/> and
/// <see cref="Roles"/> as empty/null, which collapses the role + unit
/// tiers; the Guid-suffix tier always succeeds.
/// </summary>
/// <param name="HumanId">Stable UUID of the Hat (used for the Guid-suffix tier).</param>
/// <param name="BaseName">
/// The Hat's display name (with the same null/whitespace → username
/// fallback the endpoints already apply).
/// </param>
/// <param name="UnitDisplayName">
/// The unit display name to use when the unit tier fires. <c>null</c>
/// or whitespace collapses the tier; the Guid-suffix tier fires next.
/// For a Hat with multiple memberships the caller picks one (typically
/// the alphabetically first to match the existing membership
/// ordering); the picked unit must be consistent across all candidate
/// rows so siblings compare like-with-like.
/// </param>
/// <param name="Roles">
/// The role list to use when the role tier fires. The disambiguator
/// reads the first non-empty entry; <c>null</c> or empty list
/// collapses the tier.
/// </param>
public sealed record HatLabelCandidate(
    Guid HumanId,
    string BaseName,
    string? UnitDisplayName,
    IReadOnlyList<string>? Roles);
