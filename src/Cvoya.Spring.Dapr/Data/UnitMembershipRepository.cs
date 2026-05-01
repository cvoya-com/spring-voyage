// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitMembershipRepository"/>.
/// Stores rows in the <c>unit_memberships</c> table; composite primary key
/// on <c>(tenant_id, unit_id, agent_id)</c> where both identity columns
/// are stable UUIDs (actor IDs) as of #1492.
/// </summary>
public class UnitMembershipRepository(SpringDbContext context) : IUnitMembershipRepository
{
    /// <inheritdoc />
    public async Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        // Look up via the composed query filter so rows from other
        // tenants are invisible and can never collide on upsert. The
        // DbContext stamps TenantId from the ambient ITenantContext on
        // insert, so inserts don't need to set it explicitly.
        var existing = await context.UnitMemberships
            .FirstOrDefaultAsync(
                m => m.UnitId == membership.UnitId && m.AgentId == membership.AgentId,
                cancellationToken);

        if (existing is null)
        {
            // Auto-assign primary when this is the agent's first membership
            // so every agent always has exactly one primary parent. Callers
            // cannot set IsPrimary through the wire surface — the
            // repository owns the invariant.
            var hasPrimary = await context.UnitMemberships
                .AnyAsync(
                    m => m.AgentId == membership.AgentId && m.IsPrimary,
                    cancellationToken);

            var entity = new UnitMembershipEntity
            {
                UnitId = membership.UnitId,
                AgentId = membership.AgentId,
                Model = membership.Model,
                Specialty = membership.Specialty,
                Enabled = membership.Enabled,
                ExecutionMode = membership.ExecutionMode,
                IsPrimary = !hasPrimary,
            };
            context.UnitMemberships.Add(entity);
        }
        else
        {
            existing.Model = membership.Model;
            existing.Specialty = membership.Specialty;
            existing.Enabled = membership.Enabled;
            existing.ExecutionMode = membership.ExecutionMode;
            // CreatedAt + IsPrimary preserved; UpdatedAt stamped by SaveChangesAsync audit hook.
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        var existing = await context.UnitMemberships
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentId == agentId,
                cancellationToken);

        if (existing is null)
        {
            return;
        }

        // Per #744: every agent must carry at least one unit membership at
        // all times. Refuse to delete the last membership — callers that
        // intend a full teardown must delete the agent itself (e.g.
        // `spring agent purge`), which cascades the membership rows.
        var remaining = await context.UnitMemberships
            .CountAsync(m => m.AgentId == agentId, cancellationToken);
        if (remaining <= 1)
        {
            throw new AgentMembershipRequiredException(
                agentId,
                unitId,
                $"Cannot remove agent '{agentId}' from unit '{unitId}': this is the agent's last unit membership. "
                + "Assign the agent to another unit first, or delete the agent itself.");
        }

        var wasPrimary = existing.IsPrimary;
        context.UnitMemberships.Remove(existing);

        // Promote the oldest surviving membership when removing the primary.
        // Tiebreaker (per plan §3, confirmed for v2.0): oldest CreatedAt,
        // then lexicographic UnitId (as string) — stable under unit rename
        // + deterministic.
        if (wasPrimary)
        {
            var successor = await context.UnitMemberships
                .Where(m => m.AgentId == agentId && m.UnitId != unitId)
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.UnitId)
                .FirstOrDefaultAsync(cancellationToken);

            if (successor is not null)
            {
                successor.IsPrimary = true;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .Where(m => m.AgentId == agentId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        context.UnitMemberships.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        var entity = await context.UnitMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentId == agentId,
                cancellationToken);

        return entity is null ? null : ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .AsNoTracking()
            .Where(m => m.UnitId == unitId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .AsNoTracking()
            .Where(m => m.AgentId == agentId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .AsNoTracking()
            .OrderBy(m => m.UnitId)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    private static UnitMembership ToDto(UnitMembershipEntity e) =>
        new(
            e.UnitId,
            e.AgentId,
            e.Model,
            e.Specialty,
            e.Enabled,
            e.ExecutionMode,
            e.CreatedAt,
            e.UpdatedAt,
            e.IsPrimary);
}