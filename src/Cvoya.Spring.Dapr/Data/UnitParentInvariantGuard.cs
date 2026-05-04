// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IUnitParentInvariantGuard"/> backed by
/// <see cref="SpringDbContext"/> (for the IsTopLevel lookup) and
/// <see cref="IUnitHierarchyResolver"/> (for the current parent-edge
/// count). Consults the two together so "the last parent" and "is the
/// child marked top-level" are evaluated in the same scope — a
/// non-top-level unit with a single remaining parent edge is the
/// 409-worthy case this guard rejects.
/// </summary>
public class UnitParentInvariantGuard(
    SpringDbContext db,
    IUnitHierarchyResolver hierarchyResolver) : IUnitParentInvariantGuard
{
    /// <inheritdoc />
    public async Task EnsureParentRemainsAsync(
        Address parent,
        Address child,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        // Only unit children carry the parent-required invariant — agents
        // are covered by AgentMembershipRequiredException / the
        // unit-membership repository's last-row guard.
        if (!string.Equals(child.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The unit's identity is its Guid; existence in this tenant's scope
        // is enough to indicate "registered". Top-level units (no parent
        // edges) are now expressed implicitly: a unit with zero parent
        // memberships is top-level. Skip the guard when the child carries
        // no parents at all — the resolver's empty result IS the signal.
        var unitEntity = await db.UnitDefinitions
            .FirstOrDefaultAsync(u => u.Id == child.Id, cancellationToken);
        if (unitEntity is null)
        {
            // Child is not registered in this tenant's scope — removal is
            // a no-op for the unit actor too, so there's nothing to
            // protect. Matches the idempotent RemoveMember semantics.
            return;
        }

        var currentParents = await hierarchyResolver.GetParentsAsync(
            child, cancellationToken);

        // No parent edges at all → top-level unit; removing an edge that does
        // not exist is a no-op (idempotent RemoveMember contract). Treat this
        // as the implicit top-level signal post-#1629.
        if (currentParents.Count == 0)
        {
            return;
        }

        // The edge under review is from `parent` to `child`. After removal,
        // the child keeps every other parent. If the only remaining parent
        // is `parent` itself, we're about to strip the last one.
        var remaining = currentParents.Count(p => p != parent);
        if (remaining == 0)
        {
            throw new UnitParentRequiredException(
                child.Path,
                parent.Path,
                $"Cannot remove unit '{child.Path}' from unit '{parent.Path}': this is the unit's last parent. "
                + "Attach it to another parent unit first or delete the unit itself.");
        }
    }
}