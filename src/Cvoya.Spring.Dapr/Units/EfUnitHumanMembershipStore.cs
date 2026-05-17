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
/// (ADR-0044 § 5). Creates a fresh <c>IServiceScope</c> per call so the
/// underlying scoped <see cref="SpringDbContext"/> resolves cleanly from
/// singleton callers (e.g. the MCP skill registry). Mirrors the
/// scope-per-call shape used by <see cref="UnitMemberGraphStore"/>.
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
                m.Role,
                m.Expertise,
                m.Notifications,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new UnitHumanMembership(
                MembershipId: r.Id,
                HumanId: r.HumanId,
                Role: r.Role,
                Expertise: (IReadOnlyList<string>)(r.Expertise ?? new List<string>()),
                Notifications: (IReadOnlyList<string>)(r.Notifications ?? new List<string>())))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<UnitHumanMembership?> GetAsync(
        Guid unitId,
        Guid humanId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.UnitMembershipsHumans
            .AsNoTracking()
            .Where(m => m.UnitId == unitId && m.HumanId == humanId && m.Role == role)
            .Select(m => new
            {
                m.Id,
                m.HumanId,
                m.Role,
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
            Role: row.Role,
            Expertise: (IReadOnlyList<string>)(row.Expertise ?? new List<string>()),
            Notifications: (IReadOnlyList<string>)(row.Notifications ?? new List<string>()));
    }

    /// <inheritdoc />
    public async Task<UnitHumanMembership> UpsertAsync(
        Guid unitId,
        Guid humanId,
        string role,
        IReadOnlyList<string> expertise,
        IReadOnlyList<string> notifications,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Role must be non-empty.", nameof(role));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        var trimmedRole = role.Trim();
        var expertiseList = (expertise ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToList();
        var notificationsList = (notifications ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        // Existing row → update expertise + notifications in place. The
        // tenant query filter on the DbContext scopes the lookup to the
        // current tenant; the unique index in ADR-0044 § 3 guarantees there
        // is at most one match.
        var existing = await db.UnitMembershipsHumans
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.HumanId == humanId && m.Role == trimmedRole,
                cancellationToken);

        if (existing is not null)
        {
            existing.Expertise = expertiseList;
            existing.Notifications = notificationsList;
            await db.SaveChangesAsync(cancellationToken);

            return new UnitHumanMembership(
                MembershipId: existing.Id,
                HumanId: existing.HumanId,
                Role: existing.Role,
                Expertise: expertiseList,
                Notifications: notificationsList);
        }

        var inserted = new UnitMembershipHumanEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantContext.CurrentTenantId,
            UnitId = unitId,
            HumanId = humanId,
            Role = trimmedRole,
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
            // our expertise / notifications on top so the caller still sees
            // their POSTed values reflected per the idempotency contract.
            db.Entry(inserted).State = EntityState.Detached;

            var winner = await db.UnitMembershipsHumans
                .FirstOrDefaultAsync(
                    m => m.UnitId == unitId && m.HumanId == humanId && m.Role == trimmedRole,
                    cancellationToken);
            if (winner is null)
            {
                // The DbUpdateException was not the unique-index race we
                // expected; surface the original failure.
                throw;
            }
            winner.Expertise = expertiseList;
            winner.Notifications = notificationsList;
            await db.SaveChangesAsync(cancellationToken);

            return new UnitHumanMembership(
                MembershipId: winner.Id,
                HumanId: winner.HumanId,
                Role: winner.Role,
                Expertise: expertiseList,
                Notifications: notificationsList);
        }

        return new UnitHumanMembership(
            MembershipId: inserted.Id,
            HumanId: inserted.HumanId,
            Role: inserted.Role,
            Expertise: expertiseList,
            Notifications: notificationsList);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        Guid unitId,
        Guid humanId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var trimmedRole = role.Trim();
        var existing = await db.UnitMembershipsHumans
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.HumanId == humanId && m.Role == trimmedRole,
                cancellationToken);
        if (existing is null)
        {
            return false;
        }

        db.UnitMembershipsHumans.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
