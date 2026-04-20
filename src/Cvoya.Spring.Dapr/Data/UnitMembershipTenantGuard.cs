// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IUnitMembershipTenantGuard"/> backed by
/// <see cref="SpringDbContext"/>. Uses the ambient tenant-scoped query
/// filter on <see cref="UnitDefinitionEntity"/> and
/// <see cref="AgentDefinitionEntity"/> — a row "exists" only when it
/// belongs to the current tenant, so a single <c>AnyAsync</c> against the
/// filtered DbSet answers "is this entity visible to me". Two entities
/// visible to the same <see cref="SpringDbContext"/> scope are
/// guaranteed to share a tenant by construction.
/// <para>
/// The guard deliberately does not reach into <see cref="Data.Entities.UnitMembershipEntity"/>
/// because the goal is to reject cross-tenant writes before any
/// membership row is touched — a read of <c>UnitDefinition</c> /
/// <c>AgentDefinition</c> matches the same filter the write path will
/// apply, so an unknown id on the write path also surfaces as "not in
/// my tenant" here.
/// </para>
/// </summary>
public class UnitMembershipTenantGuard(SpringDbContext db) : IUnitMembershipTenantGuard
{
    /// <inheritdoc />
    public async Task<bool> ShareTenantAsync(
        Address parent,
        Address member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(member);

        if (!string.Equals(parent.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            // The composition graph only attaches members to units — treat
            // any non-unit parent as "no edge to protect" so callers that
            // mis-route here degrade safely.
            return false;
        }

        if (!await UnitVisibleAsync(parent.Path, cancellationToken))
        {
            return false;
        }

        return member.Scheme switch
        {
            var s when string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase) =>
                await AgentVisibleAsync(member.Path, cancellationToken),
            var s when string.Equals(s, "unit", StringComparison.OrdinalIgnoreCase) =>
                await UnitVisibleAsync(member.Path, cancellationToken),
            _ => false,
        };
    }

    /// <inheritdoc />
    public async Task EnsureSameTenantAsync(
        Address parent,
        Address member,
        CancellationToken cancellationToken = default)
    {
        if (await ShareTenantAsync(parent, member, cancellationToken))
        {
            return;
        }

        // "Does not share a tenant" collapses together missing / deleted /
        // other-tenant on the read side, which is the shape we want the
        // caller to see: we do not leak whether the address exists in a
        // different tenant. The 404 endpoint mapping keeps that contract.
        throw new CrossTenantMembershipException(
            parent,
            member,
            $"Cannot add '{member}' to '{parent}': the target is not visible in this tenant.");
    }

    private Task<bool> UnitVisibleAsync(string unitId, CancellationToken cancellationToken) =>
        db.UnitDefinitions.AnyAsync(u => u.UnitId == unitId, cancellationToken);

    private Task<bool> AgentVisibleAsync(string agentId, CancellationToken cancellationToken) =>
        db.AgentDefinitions.AnyAsync(a => a.AgentId == agentId, cancellationToken);
}