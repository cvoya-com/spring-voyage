// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Restricts which AI models agents in a unit may use. Second concrete
/// <see cref="UnitPolicy"/> dimension — see #247. Shape mirrors
/// <see cref="SkillPolicy"/>: optional allow-list + optional block-list so the
/// unified policy record stays predictable for operators that have already
/// learned the skill-policy semantics.
/// </summary>
/// <remarks>
/// <para>
/// Evaluation order mirrors <see cref="SkillPolicy"/>: a model id that appears
/// in <paramref name="Blocked"/> is always denied regardless of
/// <paramref name="Allowed"/>. When <paramref name="Allowed"/> is
/// non-<c>null</c>, it acts as a whitelist — any model not in the list is
/// denied. Matching is case-insensitive.
/// </para>
/// <para>
/// The empty-list / whitelist distinction is deliberate: <c>Allowed: []</c>
/// means "no model may be used" (useful for temporary freezes), while
/// <c>Allowed: null</c> means "no whitelist constraint".
/// </para>
/// </remarks>
/// <param name="Allowed">
/// Optional whitelist of model identifiers. <c>null</c> means "no whitelist —
/// every model not in <paramref name="Blocked"/> is allowed". A non-<c>null</c>
/// list restricts model selection to its members.
/// </param>
/// <param name="Blocked">
/// Optional blacklist of model identifiers. Matching entries are always denied
/// and take precedence over <paramref name="Allowed"/>. Useful for phasing out
/// an expensive or deprecated model before removing it from inventory.
/// </param>
public record ModelPolicy(
    IReadOnlyList<string>? Allowed = null,
    IReadOnlyList<string>? Blocked = null);