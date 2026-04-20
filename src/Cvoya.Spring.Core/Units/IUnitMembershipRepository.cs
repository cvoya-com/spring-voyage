// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Persistence abstraction for the unit-membership edge introduced in #160.
/// A membership attaches one agent to one unit and carries optional
/// per-membership config overrides. An agent may have any number of
/// memberships — unit-typed members remain 1:N per #217, but the
/// <c>(unit, agent)</c> relation is M:N at the storage level.
/// </summary>
/// <remarks>
/// Defined in <c>Cvoya.Spring.Core</c> so the private cloud repo can swap
/// the implementation (e.g. a tenant-scoped wrapper) via DI without
/// taking a dependency on <c>Cvoya.Spring.Dapr</c>. The default
/// implementation lives in <c>Cvoya.Spring.Dapr.Data</c> and uses
/// <c>SpringDbContext</c>.
/// </remarks>
public interface IUnitMembershipRepository
{
    /// <summary>
    /// Creates or updates the membership row for
    /// <c>(membership.UnitId, membership.AgentAddress)</c>. Audit timestamps
    /// on <paramref name="membership"/> are ignored — the repository stamps
    /// <c>CreatedAt</c> on insert and <c>UpdatedAt</c> on every write.
    /// </summary>
    Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the membership row for the given composite key. No-op when
    /// no row matches — callers that need 404 semantics must check via
    /// <see cref="GetAsync(string, string, CancellationToken)"/> first.
    /// <para>
    /// Per #744 every agent must carry at least one unit membership. The
    /// implementation throws <see cref="AgentMembershipRequiredException"/>
    /// when removing this row would leave the agent with zero memberships;
    /// callers that intend a full teardown (e.g. delete-agent cascade)
    /// must use <see cref="DeleteAllForAgentAsync(string, CancellationToken)"/>.
    /// </para>
    /// </summary>
    Task DeleteAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-removes every membership row attached to the given agent.
    /// Bypasses the last-membership guard enforced by
    /// <see cref="DeleteAsync(string, string, CancellationToken)"/> — this
    /// is the cascade path used by delete-agent so purging an agent does
    /// not trip the "at least one membership" invariant on the final row.
    /// No-op when the agent has no memberships.
    /// </summary>
    Task DeleteAllForAgentAsync(string agentAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the membership for the given composite key, or <c>null</c>
    /// if no row exists.
    /// </summary>
    Task<UnitMembership?> GetAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every membership attached to the given unit, in stable
    /// <c>CreatedAt</c> order so callers that treat the first entry as the
    /// "primary" unit see a deterministic choice.
    /// </summary>
    Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every membership the given agent participates in, in stable
    /// <c>CreatedAt</c> order. The first entry acts as the derived parent
    /// unit for wire-compat surfaces (<c>AgentMetadata.ParentUnit</c>,
    /// <c>AgentResponse.ParentUnit</c>).
    /// </summary>
    Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(string agentAddress, CancellationToken cancellationToken = default);
}