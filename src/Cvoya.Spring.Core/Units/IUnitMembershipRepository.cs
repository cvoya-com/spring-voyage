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
/// All parameters that identify a unit or agent are stable UUIDs (actor
/// IDs) as of #1492, not slug strings. The slug-as-identity bug class is
/// described in #1488; this interface was migrated alongside the
/// <c>unit_memberships</c> table primary-key change.
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the private cloud repo can swap
/// the implementation (e.g. a tenant-scoped wrapper) via DI without
/// taking a dependency on <c>Cvoya.Spring.Dapr</c>. The default
/// implementation lives in <c>Cvoya.Spring.Dapr.Data</c> and uses
/// <c>SpringDbContext</c>.
/// </para>
/// </remarks>
public interface IUnitMembershipRepository
{
    /// <summary>
    /// Creates or updates the membership row for
    /// <c>(membership.UnitId, membership.AgentId)</c>. Audit timestamps
    /// on <paramref name="membership"/> are ignored — the repository stamps
    /// <c>CreatedAt</c> on insert and <c>UpdatedAt</c> on every write.
    /// </summary>
    Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the membership row for the given composite key. No-op when
    /// no row matches — callers that need 404 semantics must check via
    /// <see cref="GetAsync(Guid, Guid, CancellationToken)"/> first.
    /// <para>
    /// Per #744 every agent must carry at least one unit membership. The
    /// implementation throws <see cref="AgentMembershipRequiredException"/>
    /// when removing this row would leave the agent with zero memberships;
    /// callers that intend a full teardown (e.g. delete-agent cascade)
    /// must use <see cref="DeleteAllForAgentAsync(Guid, CancellationToken)"/>.
    /// </para>
    /// </summary>
    Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-removes every membership row attached to the given agent UUID.
    /// Bypasses the last-membership guard enforced by
    /// <see cref="DeleteAsync(Guid, Guid, CancellationToken)"/> — this
    /// is the cascade path used by delete-agent so purging an agent does
    /// not trip the "at least one membership" invariant on the final row.
    /// No-op when the agent has no memberships.
    /// </summary>
    Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the membership for the given composite key, or <c>null</c>
    /// if no row exists.
    /// </summary>
    Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites only the <c>roles</c> + <c>expertise</c> jsonb columns
    /// on the existing membership row for <c>(unitId, agentId)</c>.
    /// Leaves <c>model</c>, <c>specialty</c>, <c>enabled</c>,
    /// <c>executionMode</c>, and <c>isPrimary</c> untouched — these flow
    /// through <see cref="UpsertAsync(UnitMembership, CancellationToken)"/>
    /// from the existing membership-edit surface. Returns the row's
    /// post-write projection.
    /// <para>
    /// Surfaced as a dedicated method (issue #2463) so the PATCH edit
    /// surface for agent-member <c>roles</c> + <c>expertise</c> never
    /// reads-and-rewrites the other override columns — keeping the
    /// two edit paths orthogonal. Returns <see langword="null"/> when
    /// no membership row exists for the key.
    /// </para>
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="agentId">The agent's stable Guid identity.</param>
    /// <param name="roles">Replacement roles list; empty list clears.</param>
    /// <param name="expertise">Replacement expertise list; empty list clears.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<UnitMembership?> UpdateRolesAndExpertiseAsync(
        Guid unitId,
        Guid agentId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> expertise,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every membership attached to the given unit UUID, in stable
    /// <c>CreatedAt</c> order so callers that treat the first entry as the
    /// "primary" unit see a deterministic choice.
    /// </summary>
    Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every membership the given agent UUID participates in, in
    /// stable <c>CreatedAt</c> order. The first entry acts as the derived
    /// parent unit for wire-compat surfaces (<c>AgentMetadata.ParentUnit</c>,
    /// <c>AgentResponse.ParentUnit</c>).
    /// </summary>
    Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every membership visible in the current tenant scope. Used
    /// by surfaces that need the full edge set at once — notably the
    /// tenant-tree endpoint (<c>GET /api/v1/tenant/tree</c>) that renders
    /// the Explorer. Rows are ordered by <c>(UnitId, CreatedAt)</c> so
    /// callers that group by unit get a deterministic iteration order.
    /// </summary>
    Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default);
}
