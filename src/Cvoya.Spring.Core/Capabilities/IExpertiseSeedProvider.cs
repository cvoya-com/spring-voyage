// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Resolves the declarative expertise seed for an agent or unit — i.e. the
/// <c>expertise:</c> block authored in the <c>AgentDefinition</c> /
/// <c>UnitDefinition</c> YAML that was persisted on entity creation.
/// </summary>
/// <remarks>
/// <para>
/// Used at actor activation (see <c>AgentActor.OnActivateAsync</c> /
/// <c>UnitActor.OnActivateAsync</c>) to close the "silent gap" where seed
/// expertise declared in YAML was not auto-applied to actor state — an
/// operator had to PUT the same data through <c>SetExpertiseAsync</c> for it
/// to show up on <c>GET /api/v1/agents/{id}/expertise</c>. See #488.
/// </para>
/// <para>
/// Seeding is only meant for first activation. Once actor state holds an
/// explicit expertise entry (even an empty list written via HTTP PUT / CLI),
/// the actor is authoritative — the seed is not re-applied. This matches the
/// "actor-state wins" precedence rule documented in
/// <c>docs/architecture/units.md § Seeding from YAML</c>.
/// </para>
/// <para>
/// Implementations must return <c>null</c> (not an empty array) when no seed
/// is declared so callers can tell "no YAML block" apart from "declared but
/// empty". Implementations must not throw for missing entities — a missing
/// definition behaves the same as a missing seed.
/// </para>
/// </remarks>
public interface IExpertiseSeedProvider
{
    /// <summary>
    /// Reads the seed expertise declared for the given agent id.
    /// Returns <c>null</c> when no seed was declared (no definition or no
    /// <c>expertise:</c> block); an empty array means "declared empty".
    /// </summary>
    /// <param name="agentId">
    /// The Dapr actor id for the agent. The production caller is
    /// <c>AgentActor.OnActivateAsync</c>, which passes <c>Id.GetId()</c>.
    /// Implementations match on the actor id alone; a user-facing-name
    /// lookup would need a dedicated overload (#519).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ExpertiseDomain>?> GetAgentSeedAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the seed own-expertise declared for the given unit id.
    /// Returns <c>null</c> when no seed was declared; an empty array means
    /// "declared empty".
    /// </summary>
    /// <param name="unitId">
    /// The Dapr actor id for the unit. The production caller is
    /// <c>UnitActor.OnActivateAsync</c>, which passes <c>Id.GetId()</c>.
    /// Implementations match on the actor id alone; a user-facing-name
    /// lookup would need a dedicated overload (#519).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ExpertiseDomain>?> GetUnitSeedAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}