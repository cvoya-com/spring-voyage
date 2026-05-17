// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;

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
}
