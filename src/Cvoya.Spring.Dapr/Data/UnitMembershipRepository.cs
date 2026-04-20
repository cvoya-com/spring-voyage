// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed implementation of <see cref="IUnitMembershipRepository"/>.
/// Stores rows in the <c>unit_memberships</c> table; composite primary key
/// on <c>(unit_id, agent_address)</c>.
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
                m => m.UnitId == membership.UnitId && m.AgentAddress == membership.AgentAddress,
                cancellationToken);

        if (existing is null)
        {
            var entity = new UnitMembershipEntity
            {
                UnitId = membership.UnitId,
                AgentAddress = membership.AgentAddress,
                Model = membership.Model,
                Specialty = membership.Specialty,
                Enabled = membership.Enabled,
                ExecutionMode = membership.ExecutionMode,
            };
            context.UnitMemberships.Add(entity);
        }
        else
        {
            existing.Model = membership.Model;
            existing.Specialty = membership.Specialty;
            existing.Enabled = membership.Enabled;
            existing.ExecutionMode = membership.ExecutionMode;
            // CreatedAt preserved; UpdatedAt stamped by SaveChangesAsync audit hook.
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default)
    {
        var existing = await context.UnitMemberships
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentAddress == agentAddress,
                cancellationToken);

        if (existing is null)
        {
            return;
        }

        context.UnitMemberships.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UnitMembership?> GetAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default)
    {
        var entity = await context.UnitMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UnitId == unitId && m.AgentAddress == agentAddress,
                cancellationToken);

        return entity is null ? null : ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .AsNoTracking()
            .Where(m => m.UnitId == unitId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(string agentAddress, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitMemberships
            .AsNoTracking()
            .Where(m => m.AgentAddress == agentAddress)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    private static UnitMembership ToDto(UnitMembershipEntity e) =>
        new(
            e.UnitId,
            e.AgentAddress,
            e.Model,
            e.Specialty,
            e.Enabled,
            e.ExecutionMode,
            e.CreatedAt,
            e.UpdatedAt);
}