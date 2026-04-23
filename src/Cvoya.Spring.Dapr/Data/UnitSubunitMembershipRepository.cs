// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitSubunitMembershipRepository"/>.
/// Stores rows in the <c>unit_subunit_memberships</c> table; composite
/// primary key on <c>(tenant_id, parent_unit_id, child_unit_id)</c>.
/// </summary>
public class UnitSubunitMembershipRepository(SpringDbContext context) : IUnitSubunitMembershipRepository
{
    /// <inheritdoc />
    public async Task UpsertAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentUnitId);
        ArgumentException.ThrowIfNullOrEmpty(childUnitId);

        var existing = await context.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentUnitId == parentUnitId && e.ChildUnitId == childUnitId,
                cancellationToken);

        if (existing is null)
        {
            context.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                ParentUnitId = parentUnitId,
                ChildUnitId = childUnitId,
            });
        }
        else
        {
            // Touch the row so the audit hook stamps UpdatedAt — keeps
            // the projection's freshness signal in sync with the
            // last-known actor write even when the edge itself is
            // unchanged. Without an actual property mutation EF Core
            // skips the row entirely; bump UpdatedAt explicitly so the
            // audit hook sees a Modified entry on the next iteration.
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default)
    {
        var existing = await context.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentUnitId == parentUnitId && e.ChildUnitId == childUnitId,
                cancellationToken);

        if (existing is null)
        {
            return;
        }

        context.UnitSubunitMemberships.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAllForUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .Where(e => e.ParentUnitId == unitId || e.ChildUnitId == unitId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        context.UnitSubunitMemberships.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListByParentAsync(string parentUnitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ParentUnitId == parentUnitId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ChildUnitId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListByChildAsync(string childUnitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ChildUnitId == childUnitId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ParentUnitId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .OrderBy(e => e.ParentUnitId)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    private static UnitSubunitMembership ToDto(UnitSubunitMembershipEntity e) =>
        new(e.ParentUnitId, e.ChildUnitId, e.CreatedAt, e.UpdatedAt);
}