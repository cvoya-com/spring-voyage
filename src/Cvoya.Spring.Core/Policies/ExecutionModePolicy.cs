// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Restricts which <see cref="AgentExecutionMode"/> values agents in a unit
/// may run under. Fourth concrete <see cref="UnitPolicy"/> dimension — see
/// #249. The unit owns dispatch governance: if the unit demands every member
/// run <c>OnDemand</c> (no auto-routing) the per-membership and agent-global
/// values are overridden at dispatch time.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes are supported, with well-defined interaction:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <paramref name="Forced"/> pins every agent in the unit to a single
///     execution mode. When non-<c>null</c> it overrides any per-membership
///     or agent-global configuration. Useful for a production unit that must
///     never surprise operators with autonomous routing.
///     </description>
///   </item>
///   <item>
///     <description>
///     <paramref name="Allowed"/> acts as a whitelist. A request to dispatch
///     an agent under a mode not in the list is denied. When
///     <paramref name="Forced"/> is non-<c>null</c>, <paramref name="Allowed"/>
///     is ignored: forcing a mode already implies a single-element allow list.
///     </description>
///   </item>
/// </list>
/// <para>
/// Unit policy wins over per-membership and agent-global overrides. This
/// matches the precedence documented on <see cref="UnitPolicy"/>: a unit is a
/// governance boundary, not an advisor.
/// </para>
/// </remarks>
/// <param name="Forced">
/// Optional pinned execution mode. Non-<c>null</c> coerces every agent in the
/// unit to this mode on dispatch regardless of their own declaration or any
/// per-membership override.
/// </param>
/// <param name="Allowed">
/// Optional whitelist of permitted execution modes. <c>null</c> means "no
/// whitelist constraint". Ignored when <paramref name="Forced"/> is
/// non-<c>null</c>.
/// </param>
public record ExecutionModePolicy(
    AgentExecutionMode? Forced = null,
    IReadOnlyList<AgentExecutionMode>? Allowed = null);