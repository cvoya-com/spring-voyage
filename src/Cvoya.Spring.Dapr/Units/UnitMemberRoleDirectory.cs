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
/// Default singleton implementation of <see cref="IUnitMemberRoleDirectory"/>
/// (#3089). Resolves agent-member effective roles with a single join over
/// <c>unit_memberships</c> ⨝ <c>agent_definitions</c> instead of the
/// per-member definition reads + parallel membership passes the directory
/// surfaces used before this seam existed. Creates a fresh
/// <c>IServiceScope</c> per call so the scoped <see cref="SpringDbContext"/>
/// resolves cleanly from the singleton-style activation, mirroring
/// <see cref="UnitMemberGraphStore"/>.
/// </summary>
public sealed class UnitMemberRoleDirectory(IServiceScopeFactory scopeFactory)
    : IUnitMemberRoleDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAgentMemberRolesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // One join: the membership edge carries the per-membership role
        // labels; the agent definition carries the by-reference role. Both
        // sources for "this agent member's roles" come back in a single
        // round-trip (tenant scoping is applied by each entity's query
        // filter). A left join keeps members whose definition row is
        // missing — those resolve to their membership roles only.
        var rows = await (
            from m in db.UnitMemberships.AsNoTracking()
            where m.UnitId == unitId
            join a in db.AgentDefinitions.AsNoTracking()
                on m.AgentId equals a.Id into defs
            from def in defs.DefaultIfEmpty()
            orderby m.CreatedAt, m.AgentId
            select new
            {
                m.AgentId,
                MembershipRoles = m.Roles,
                AgentRole = def != null ? def.Role : null,
            }).ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var row in rows)
        {
            // An agent appears at most once per unit (composite PK on
            // (tenant, unit, agent)), so no per-agent union across rows is
            // needed here — that is the cross-unit concern handled by
            // GetAgentEffectiveRolesAsync.
            var effective = EffectiveRolePolicy.Combine(row.MembershipRoles, row.AgentRole);
            if (effective.Count > 0)
            {
                result[row.AgentId] = effective;
            }
        }

        return result;
    }
}
