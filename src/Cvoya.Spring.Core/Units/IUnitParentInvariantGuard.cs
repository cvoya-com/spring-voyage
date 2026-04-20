// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Enforces the "every unit has a parent" invariant on the edge-removal
/// path (review feedback on #744). Mirrors
/// <see cref="IUnitMembershipTenantGuard"/> in shape: a single scoped
/// service the endpoints consult before they touch the unit actor.
/// <para>
/// The guard answers a single question — "would dropping the edge
/// (<c>parent</c> → <c>child</c>) leave <c>child</c> with zero
/// parent-unit edges while it is not marked top-level?" — so the call
/// site can decide whether to raise a 409 or proceed with the removal.
/// Top-level units have no parent-unit edges by design; the check is a
/// no-op for them. Agent children are a no-op too — the agent-membership
/// guard already covers them.
/// </para>
/// </summary>
public interface IUnitParentInvariantGuard
{
    /// <summary>
    /// Throws <see cref="UnitParentRequiredException"/> when removing the
    /// edge from <paramref name="parent"/> to <paramref name="child"/>
    /// would leave <paramref name="child"/> parentless despite not being
    /// a top-level unit. Returns normally when:
    /// <list type="bullet">
    ///   <item><description><paramref name="child"/> is not a unit (e.g. agent);</description></item>
    ///   <item><description><paramref name="child"/> is marked top-level in the unit-definition table;</description></item>
    ///   <item><description><paramref name="child"/> still has at least one other parent-unit edge;</description></item>
    ///   <item><description><paramref name="child"/> is not registered (removal is a no-op regardless).</description></item>
    /// </list>
    /// </summary>
    /// <param name="parent">The parent unit whose edge is about to be dropped.</param>
    /// <param name="child">The member address losing the edge.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task EnsureParentRemainsAsync(
        Address parent,
        Address child,
        CancellationToken cancellationToken = default);
}