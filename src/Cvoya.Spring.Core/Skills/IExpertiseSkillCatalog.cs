// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Live catalog of expertise-directory-driven skills (#359). Queries the
/// canonical expertise data (<see cref="IExpertiseAggregator"/> +
/// <see cref="IExpertiseStore"/>, shipped by #487 / #497) on every
/// enumeration so directory mutations (agent gains expertise, unit projection
/// changes) propagate without any snapshot or restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligibility.</b> Only entries with a non-null
/// <see cref="ExpertiseDomain.InputSchemaJson"/> are surfaced — consultative
/// expertise with no typed contract stays message-only.
/// </para>
/// <para>
/// <b>Boundary.</b> Outside-callers see only the projected (boundary-applied)
/// view of a unit — unit-level projections become externally callable skills;
/// agent-level expertise inside a unit that isn't covered by a unit-level
/// projection is visible only when the caller is inside the boundary.
/// </para>
/// <para>
/// <b>Naming.</b> Skill names use the <c>expertise/{slug}</c> scheme
/// (see <see cref="ExpertiseSkillNaming"/>) so agent names never appear on
/// the skill surface — an agent swap under the same expertise entry keeps the
/// skill name stable.
/// </para>
/// </remarks>
public interface IExpertiseSkillCatalog
{
    /// <summary>
    /// Enumerates every skill visible to the caller described by
    /// <paramref name="context"/>. Queries live expertise data on every call.
    /// </summary>
    /// <param name="context">
    /// Caller context. External callers (<see cref="BoundaryViewContext.External"/>)
    /// see only the projected view — agent-level expertise that isn't unit-
    /// projected is hidden. Inside callers (<see cref="BoundaryViewContext.InsideUnit"/>)
    /// see the raw aggregated view.
    /// </param>
    /// <param name="cancellationToken">A token to cancel enumeration.</param>
    Task<IReadOnlyList<ExpertiseSkill>> EnumerateAsync(
        BoundaryViewContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a catalog skill name back to its <see cref="ExpertiseSkill"/>.
    /// Uses the same live enumeration as <see cref="EnumerateAsync"/>,
    /// honouring the caller's boundary context so a skill hidden from the
    /// caller resolves to <c>null</c> (defence in depth — the catalog never
    /// leaks a target the caller cannot see).
    /// </summary>
    /// <param name="skillName">The catalog skill name (<c>expertise/{slug}</c>).</param>
    /// <param name="context">Caller context, as for <see cref="EnumerateAsync"/>.</param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    Task<ExpertiseSkill?> ResolveAsync(
        string skillName,
        BoundaryViewContext context,
        CancellationToken cancellationToken = default);
}
