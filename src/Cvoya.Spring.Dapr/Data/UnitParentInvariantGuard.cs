// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IUnitParentInvariantGuard"/> backed by
/// <see cref="SpringDbContext"/> and
/// <see cref="IUnitSubunitMembershipRepository"/>. Reads the child unit's
/// existing edges directly from <c>unit_subunit_memberships</c> so the
/// guard agrees with the EF projection that drives the rest of the
/// hierarchy (#2052 / ADR-0040).
/// </summary>
/// <remarks>
/// <para>
/// Top-level signal: a unit is top-level iff <c>unit_subunit_memberships</c>
/// has at least one row with <c>parent_id == tenantId</c> for that
/// child. This is the explicit tenant-root edge introduced in #2052;
/// previously top-level was inferred from "no parent edges at all".
/// </para>
/// </remarks>
public class UnitParentInvariantGuard(
    SpringDbContext db,
    IUnitSubunitMembershipRepository subunitRepository,
    ITenantContext tenantContext) : IUnitParentInvariantGuard
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
        if (!string.Equals(child.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var unitEntity = await db.UnitDefinitions
            .FirstOrDefaultAsync(u => u.Id == child.Id, cancellationToken);
        if (unitEntity is null)
        {
            // Child is not registered in this tenant's scope — removal is
            // a no-op for the unit actor too, so there's nothing to
            // protect. Matches the idempotent RemoveMember semantics.
            return;
        }

        // Read every parent edge for the child. Includes the tenant-root
        // edge (#2052), which we treat as the top-level marker — an
        // edge whose parent is the tenant id signals "this unit is
        // deliberately tenant-parented" and exempts the child from the
        // last-parent invariant.
        var allEdges = await subunitRepository.ListByChildAsync(child.Id, cancellationToken);
        var tenantId = tenantContext.CurrentTenantId;

        var hasTenantRootEdge = allEdges.Any(e => e.ParentId == tenantId);
        if (hasTenantRootEdge)
        {
            return;
        }

        // No tenant-root edge ⇒ child must have at least one unit
        // parent. Compute the remaining unit parents after removing the
        // edge under review.
        var remainingUnitParents = allEdges
            .Where(e => e.ParentId != tenantId)
            .Count(e => e.ParentId != parent.Id);

        if (remainingUnitParents == 0)
        {
            throw new UnitParentRequiredException(
                child.Path,
                parent.Path,
                $"Cannot remove unit '{child.Path}' from unit '{parent.Path}': this is the unit's last parent. "
                + "Attach it to another parent unit first or delete the unit itself.");
        }
    }
}
