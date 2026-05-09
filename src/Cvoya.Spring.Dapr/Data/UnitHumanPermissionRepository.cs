// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitHumanPermissionRepository"/>.
/// Stores rows in <c>unit_human_permissions</c>; the unique
/// <c>(tenant_id, unit_id, human_id)</c> index ensures one direct grant per
/// pair. The <c>SpringDbContext</c> stamps <c>TenantId</c> from the ambient
/// <c>ITenantContext</c> on insert.
/// </summary>
public class UnitHumanPermissionRepository(SpringDbContext context) : IUnitHumanPermissionRepository
{
    /// <inheritdoc />
    public async Task UpsertAsync(
        Guid unitId, Guid humanId, UnitPermissionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Tenant query filter scopes the lookup to the current tenant — a
        // matching row from another tenant cannot collide on the unique
        // index, even though the database-level constraint covers all
        // tenants.
        var existing = await context.UnitHumanPermissions
            .FirstOrDefaultAsync(p => p.UnitId == unitId && p.HumanId == humanId, cancellationToken);

        if (existing is null)
        {
            context.UnitHumanPermissions.Add(new UnitHumanPermissionEntity
            {
                Id = Guid.NewGuid(),
                UnitId = unitId,
                HumanId = humanId,
                PermissionLevel = entry.Permission,
                Identity = entry.Identity,
                Notifications = entry.Notifications,
                GrantedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.PermissionLevel = entry.Permission;
            existing.Identity = entry.Identity;
            existing.Notifications = entry.Notifications;
            // GrantedAt preserved across updates — it captures the original
            // grant time, not the most recent edit. The audit trail of
            // mutations lives in the activity-event stream.
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default)
    {
        var existing = await context.UnitHumanPermissions
            .FirstOrDefaultAsync(p => p.UnitId == unitId && p.HumanId == humanId, cancellationToken);

        if (existing is null)
        {
            return false;
        }

        context.UnitHumanPermissions.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<UnitPermissionEntry?> GetAsync(
        Guid unitId, Guid humanId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitHumanPermissions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UnitId == unitId && p.HumanId == humanId, cancellationToken);

        return row is null ? null : ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitPermissionEntry>> ListByUnitAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitHumanPermissions
            .AsNoTracking()
            .Where(p => p.UnitId == unitId)
            .OrderBy(p => p.GrantedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToEntry).ToList();
    }

    private static UnitPermissionEntry ToEntry(UnitHumanPermissionEntity e) =>
        new(e.HumanId.ToString(), e.PermissionLevel, e.Identity, e.Notifications);
}
