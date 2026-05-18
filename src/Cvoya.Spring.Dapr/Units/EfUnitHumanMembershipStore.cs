// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default singleton implementation of <see cref="IUnitHumanMembershipStore"/>
/// (ADR-0044 § 5 + ADR-0045 §7). Creates a fresh <c>IServiceScope</c> per
/// call so the underlying scoped <see cref="SpringDbContext"/> resolves
/// cleanly from singleton callers (e.g. the MCP skill registry). Mirrors
/// the scope-per-call shape used by <see cref="UnitMemberGraphStore"/>.
/// </summary>
public sealed class EfUnitHumanMembershipStore(
    IServiceScopeFactory scopeFactory) : IUnitHumanMembershipStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitHumanMembership>> ListByUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.HumanId,
                m.Roles,
                m.Expertise,
                m.Notifications,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new UnitHumanMembership(
                MembershipId: r.Id,
                HumanId: r.HumanId,
                Roles: (IReadOnlyList<string>)(r.Roles ?? new List<string>()),
                Expertise: (IReadOnlyList<string>)(r.Expertise ?? new List<string>()),
                Notifications: (IReadOnlyList<string>)(r.Notifications ?? new List<string>())))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UnitHumanMembership?> GetAsync(
        Guid unitId,
        Guid humanId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId && m.HumanId == humanId)
            .Select(m => new
            {
                m.Id,
                m.HumanId,
                m.Roles,
                m.Expertise,
                m.Notifications,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new UnitHumanMembership(
            MembershipId: row.Id,
            HumanId: row.HumanId,
            Roles: (IReadOnlyList<string>)(row.Roles ?? new List<string>()),
            Expertise: (IReadOnlyList<string>)(row.Expertise ?? new List<string>()),
            Notifications: (IReadOnlyList<string>)(row.Notifications ?? new List<string>()));
    }

    /// <inheritdoc />
    public async Task<UnitHumanMembership> UpsertAsync(
        Guid unitId,
        Guid humanId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> expertise,
        IReadOnlyList<string> notifications,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        var rolesList = Normalise(roles);
        var expertiseList = Normalise(expertise);
        var notificationsList = Normalise(notifications);

        // Existing row → update roles + expertise + notifications in place.
        // The tenant query filter on the DbContext scopes the lookup to the
        // current tenant; the unique index in ADR-0045 §7 guarantees there
        // is at most one match per (unit, human).
        var existing = await db.UnitMembershipsHumans
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.HumanId == humanId,
                cancellationToken);

        if (existing is not null)
        {
            existing.Roles = rolesList;
            existing.Expertise = expertiseList;
            existing.Notifications = notificationsList;
            await db.SaveChangesAsync(cancellationToken);

            return new UnitHumanMembership(
                MembershipId: existing.Id,
                HumanId: existing.HumanId,
                Roles: rolesList,
                Expertise: expertiseList,
                Notifications: notificationsList);
        }

        var inserted = new UnitMembershipHumanEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantContext.CurrentTenantId,
            UnitId = unitId,
            HumanId = humanId,
            Roles = new List<string>(rolesList),
            Expertise = new List<string>(expertiseList),
            Notifications = new List<string>(notificationsList),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.UnitMembershipsHumans.Add(inserted);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent UpsertAsync for the same
            // natural key; detach and re-read the winning row, then apply
            // our roles / expertise / notifications on top so the caller
            // still sees their POSTed values reflected per the idempotency
            // contract.
            db.Entry(inserted).State = EntityState.Detached;

            var winner = await db.UnitMembershipsHumans
                .FirstOrDefaultAsync(
                    m => m.UnitId == unitId && m.HumanId == humanId,
                    cancellationToken);
            if (winner is null)
            {
                // The DbUpdateException was not the unique-index race we
                // expected; surface the original failure.
                throw;
            }
            winner.Roles = rolesList;
            winner.Expertise = expertiseList;
            winner.Notifications = notificationsList;
            await db.SaveChangesAsync(cancellationToken);

            return new UnitHumanMembership(
                MembershipId: winner.Id,
                HumanId: winner.HumanId,
                Roles: rolesList,
                Expertise: expertiseList,
                Notifications: notificationsList);
        }

        return new UnitHumanMembership(
            MembershipId: inserted.Id,
            HumanId: inserted.HumanId,
            Roles: rolesList,
            Expertise: expertiseList,
            Notifications: notificationsList);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid unitId,
        Guid humanId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitMembershipsHumans
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.HumanId == humanId,
                cancellationToken);
        if (existing is null)
        {
            return false;
        }

        db.UnitMembershipsHumans.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static List<string> Normalise(IReadOnlyList<string>? raw)
        => (raw ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
}
