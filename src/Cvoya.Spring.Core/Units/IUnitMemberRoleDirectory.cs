// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single DB-backed seam that resolves a unit's agent members and their
/// <em>effective</em> roles from the two sources the model has by design
/// (#3089). A member's role comes from a different table depending on how
/// the member was declared:
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Agent members are by reference</b> (<c>- agent: staff-writer</c>).
///     The role lives on the agent's own definition
///     (<c>agent_definitions.role</c>); the <c>unit_memberships</c> row is
///     just the edge and may additionally carry free-form per-membership
///     role labels.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Other members carry their role on the membership row</b>
///     (e.g. human members on <c>unit_memberships_humans</c>). Those rows
///     are resolved by the human-membership seam and are out of scope for
///     this resolver, which covers the agent ⨝ definition join only.
///     </description>
///   </item>
/// </list>
/// <para>
/// "This agent member's roles" is therefore
/// <c>unit_memberships.roles ∪ agent_definitions.role</c> — the rule
/// <see cref="EffectiveRolePolicy.Combine"/> applies once. Before #3089
/// that fold lived in several independent call sites that each read the
/// membership rows separately (the <c>sv.directory.list_members</c>
/// per-membership supplement and a parallel agent→roles index for
/// <c>sv.directory.list</c>), so "effective roles" had more than one
/// implementation and could drift. This seam computes it once, from a
/// single join, so every directory surface shares the same answer.
/// </para>
/// </summary>
/// <remarks>
/// Defined in <c>Cvoya.Spring.Core</c> so the private cloud repo can swap
/// the implementation (e.g. a tenant-scoped or cached wrapper) via DI
/// without taking a dependency on <c>Cvoya.Spring.Dapr</c>. The default
/// implementation lives in <c>Cvoya.Spring.Dapr.Units</c> and reads through
/// <c>SpringDbContext</c>. Production DI registers the default with
/// <c>TryAddSingleton</c> per the extensibility rules so a cloud
/// registration takes precedence when present.
/// </remarks>
public interface IUnitMemberRoleDirectory
{
    /// <summary>
    /// Resolves the effective roles for every agent member of
    /// <paramref name="unitId"/> in a single join over
    /// <c>unit_memberships</c> ⨝ <c>agent_definitions</c>. The returned
    /// map is keyed by agent id; each value is the agent's effective roles
    /// (<see cref="EffectiveRolePolicy.Combine"/> applied to the membership
    /// row's roles and the agent's definition-level role). Agents with no
    /// effective roles are omitted from the map so a caller can treat
    /// "absent" as "no roles" without materialising empty lists. The
    /// address-keyed single-entry surfaces (<c>get_member</c>,
    /// <c>lookup</c>) resolve an agent without a containing unit, so they
    /// apply <see cref="EffectiveRolePolicy.Combine"/> directly to the
    /// agent's definition-level role rather than calling this method.
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAgentMemberRolesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default);
}
