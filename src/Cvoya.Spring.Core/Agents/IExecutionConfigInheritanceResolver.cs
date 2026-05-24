// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Resolves an agent's effective <see cref="AgentExecutionConfig"/> from its
/// own (possibly partial) configuration plus the resolved configurations of
/// its parent unit(s), enforcing the multi-parent inheritance rule from
/// ADR-0039 §6.
/// </summary>
/// <remarks>
/// <para>
/// The resolver is consumed at every surface that can change the
/// agent → parent set: agent create, execution-config update, agent assign /
/// un-assign on a unit, and the unit-side sub-unit assign / un-assign
/// (a unit can also be a member of another unit; the same rule applies).
/// </para>
/// <para>
/// Field-level rules:
/// </para>
/// <list type="bullet">
///   <item>An explicit value on the agent always wins (no inheritance for
///   that field).</item>
///   <item>For a field left to inherit: if every parent's resolved config
///   agrees, the agent inherits that value; if any parent diverges, the
///   resolver returns the diverging field on
///   <see cref="InheritanceResolution.ConflictingFields"/> and callers
///   reject the operation.</item>
///   <item>Top-level agents (no parent unit) inherit from tenant defaults —
///   the tenant scope is supplied via <c>tenantId</c> so the implementation
///   can read those defaults.</item>
/// </list>
/// <para>
/// Implementations live in <c>Cvoya.Spring.Dapr</c>; the cloud overlay may
/// substitute a tenant-aware variant via DI per the platform's
/// interface-first / <c>TryAdd*</c> rule.
/// </para>
/// </remarks>
public interface IExecutionConfigInheritanceResolver
{
    /// <summary>
    /// Resolves the agent's effective execution config.
    /// </summary>
    /// <param name="agentOwn">
    /// The agent's own configuration. Fields the operator left unset are the
    /// candidates for inheritance.
    /// </param>
    /// <param name="parentUnitIds">
    /// The agent's parent units. Empty for a top-level (tenant-parented)
    /// agent, in which case the resolver inherits from tenant defaults.
    /// </param>
    /// <param name="tenantId">
    /// The tenant the agent belongs to. Used for tenant-default fall-through
    /// per ADR-0039 §6 ("top-level entities are tenant-parented").
    /// </param>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>
    /// A populated <see cref="InheritanceResolution"/>. Callers must consult
    /// <see cref="InheritanceResolution.ConflictingFields"/> before using
    /// <see cref="InheritanceResolution.Effective"/>.
    /// </returns>
    Task<InheritanceResolution> ResolveAgentConfigAsync(
        AgentExecutionConfig agentOwn,
        IReadOnlyList<Guid> parentUnitIds,
        Guid tenantId,
        CancellationToken ct);
}
