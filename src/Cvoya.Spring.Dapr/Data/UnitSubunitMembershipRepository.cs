// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitSubunitMembershipRepository"/>.
/// Stores rows in the <c>unit_subunit_memberships</c> table; composite
/// primary key on <c>(tenant_id, parent_id, child_id)</c>.
/// </summary>
public class UnitSubunitMembershipRepository(SpringDbContext context) : IUnitSubunitMembershipRepository
{
    /// <inheritdoc />
    public async Task UpsertAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        if (parentId == Guid.Empty)
        {
            throw new ArgumentException("Parent id must not be Guid.Empty.", nameof(parentId));
        }

        if (childId == Guid.Empty)
        {
            throw new ArgumentException("Child id must not be Guid.Empty.", nameof(childId));
        }

        var existing = await context.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentId == parentId && e.ChildId == childId,
                cancellationToken);

        if (existing is null)
        {
            context.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                ParentId = parentId,
                ChildId = childId,
            });
        }
        else
        {
            // Touch the row so the audit hook stamps UpdatedAt — keeps
            // the projection's freshness signal in sync with the
            // last-known actor write even when the edge itself is
            // unchanged.
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        var existing = await context.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentId == parentId && e.ChildId == childId,
                cancellationToken);

        if (existing is null)
        {
            return;
        }

        context.UnitSubunitMemberships.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAllForUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .Where(e => e.ParentId == unitId || e.ChildId == unitId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        context.UnitSubunitMemberships.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListByParentAsync(Guid parentId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ParentId == parentId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ChildId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListByChildAsync(Guid childId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ChildId == childId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ParentId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitSubunitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitSubunitMemberships
            .AsNoTracking()
            .OrderBy(e => e.ParentId)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    private static UnitSubunitMembership ToDto(UnitSubunitMembershipEntity e) =>
        new(e.ParentId, e.ChildId, e.CreatedAt, e.UpdatedAt);
}
