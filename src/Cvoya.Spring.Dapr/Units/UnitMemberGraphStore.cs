// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System.Diagnostics;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitMemberGraphStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the underlying scoped
/// <see cref="SpringDbContext"/> resolves cleanly from the unit actor's
/// singleton-style activation. Mirrors
/// <see cref="Cvoya.Spring.Dapr.Auth.UnitHumanPermissionStore"/> /
/// <see cref="Cvoya.Spring.Dapr.Units.UnitConnectorBindingStore"/>: a
/// scope-per-call wrapper so an actor (which is not request-scoped) can
/// drive EF without leaking DI plumbing into the actor type.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0040 § 3 the EF read on the activation path is instrumented
/// with a <see cref="Stopwatch"/> + <see cref="ILogger.LogDebug"/> so the
/// v0.2 cache decision is data-driven. The instrumentation lives on
/// <see cref="GetMembersAsync"/> because that is the call <c>UnitActor</c>
/// makes during membership reads — the activation pre-warm itself does
/// not exist after this PR (the actor reads on demand instead of on
/// activate).
/// </para>
/// </remarks>
public class UnitMemberGraphStore(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitMemberGraphStore> logger) : IUnitMemberGraphStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetMembersAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var agentRows = await db.UnitMemberships
            .AsNoTracking()
            .Where(m => m.UnitId == unitId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.AgentId)
            .Select(m => new { m.AgentId, m.CreatedAt })
            .ToListAsync(cancellationToken);

        // Sub-unit children: every row whose parent_id is this unit.
        // Tenant-root edges live on the child side of THIS unit, not the
        // parent side, so they cannot leak into the member set even
        // though the table is polymorphic on ParentId.
        var subunitRows = await db.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ParentId == unitId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ChildId)
            .Select(e => new { e.ChildId, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var members = new List<Address>(agentRows.Count + subunitRows.Count);
        foreach (var row in agentRows)
        {
            members.Add(new Address(Address.AgentScheme, row.AgentId));
        }
        foreach (var row in subunitRows)
        {
            members.Add(new Address(Address.UnitScheme, row.ChildId));
        }

        sw.Stop();
        logger.LogDebug(
            "Unit {UnitId} GetMembersAsync read {AgentCount} agents + {SubunitCount} sub-units in {ElapsedMs}ms",
            unitId, agentRows.Count, subunitRows.Count, sw.ElapsedMilliseconds);

        return members;
    }

    /// <inheritdoc />
    public async Task<bool> AddAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("unitId must not be empty.", nameof(unitId));
        }

        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agentId must not be empty.", nameof(agentId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitMemberships
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentId == agentId,
                cancellationToken);

        if (existing is not null)
        {
            return false;
        }

        // Auto-assign IsPrimary on the agent's first membership so every
        // agent always has exactly one primary parent. Mirrors
        // UnitMembershipRepository's invariant.
        var hasPrimary = await db.UnitMemberships
            .AnyAsync(m => m.AgentId == agentId && m.IsPrimary, cancellationToken);

        db.UnitMemberships.Add(new UnitMembershipEntity
        {
            UnitId = unitId,
            AgentId = agentId,
            Enabled = true,
            IsPrimary = !hasPrimary,
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        if (parentId == Guid.Empty)
        {
            throw new ArgumentException("parentId must not be empty.", nameof(parentId));
        }

        if (childId == Guid.Empty)
        {
            throw new ArgumentException("childId must not be empty.", nameof(childId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentId == parentId && e.ChildId == childId,
                cancellationToken);

        if (existing is not null)
        {
            return false;
        }

        // #2052: when a unit gains its first concrete unit parent edge,
        // retire any pre-existing tenant-root edge so a child unit is
        // never simultaneously top-level and parented. The tenant-root
        // edge is the explicit "no unit parent" marker (parent = tenant
        // id); replacing it with a unit-parent edge keeps the
        // "every unit has exactly one parent" invariant intact.
        var tenantContext = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Tenancy.ITenantContext>();
        var tenantId = tenantContext.CurrentTenantId;
        if (tenantId != Guid.Empty)
        {
            var tenantRootEdge = await db.UnitSubunitMemberships
                .FirstOrDefaultAsync(
                    e => e.ParentId == tenantId && e.ChildId == childId,
                    cancellationToken);
            if (tenantRootEdge is not null)
            {
                db.UnitSubunitMemberships.Remove(tenantRootEdge);
            }
        }

        db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
        {
            ParentId = parentId,
            ChildId = childId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitMemberships
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentId == agentId,
                cancellationToken);

        if (existing is null)
        {
            return false;
        }

        var wasPrimary = existing.IsPrimary;
        db.UnitMemberships.Remove(existing);

        // Promote the oldest surviving membership when removing the
        // primary so the agent always has exactly one primary parent
        // (matches UnitMembershipRepository.DeleteAsync behaviour).
        if (wasPrimary)
        {
            var successor = await db.UnitMemberships
                .Where(m => m.AgentId == agentId && m.UnitId != unitId)
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.UnitId)
                .FirstOrDefaultAsync(cancellationToken);

            if (successor is not null)
            {
                successor.IsPrimary = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentId == parentId && e.ChildId == childId,
                cancellationToken);

        if (existing is null)
        {
            return false;
        }

        db.UnitSubunitMemberships.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListDirectSubunitChildrenAsync(
        Guid parentId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        return await db.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ParentId == parentId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.ChildId)
            .Select(e => e.ChildId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureTopLevelEdgeAsync(
        Guid unitId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("unitId must not be empty.", nameof(unitId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenantId must not be empty.", nameof(tenantId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.UnitSubunitMemberships
            .FirstOrDefaultAsync(
                e => e.ParentId == tenantId && e.ChildId == unitId,
                cancellationToken);

        if (existing is not null)
        {
            // Idempotent — the tenant-root edge already exists.
            return;
        }

        db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
        {
            ParentId = tenantId,
            ChildId = unitId,
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Top-level edge ensured: tenant {TenantId} -> unit {UnitId}",
            tenantId, unitId);
    }
}
