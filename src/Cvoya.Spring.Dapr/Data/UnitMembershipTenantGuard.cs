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

        if (!await UnitVisibleAsync(parent.Id, cancellationToken))
        {
            return false;
        }

        // #3132 / #2084: this guard's sole concern is tenant visibility, so
        // it asks the kind-agnostic question — "is this id a tenant-visible
        // agent OR unit?" — rather than switching on the caller-claimed
        // member.Scheme. The old switch keyed off the scheme, so a mis-scheme'd
        // body (e.g. {scheme:"unit", path:<agent-id>}) was checked against the
        // wrong table and rejected here with a tenant-framed message.
        // Decoupling kind from this seam lets the endpoint (AddMemberAsync)
        // resolve `scheme:id` against the claimed scheme's table and return a
        // precise, uniform "No <scheme> with id X" 404 when the id is absent,
        // deleted, or of a different kind (#3132 option (a)) — while this seam
        // still enforces the cross-tenant boundary.
        //
        // This deliberately does NOT route through
        // IDirectoryService.ResolveKindAsync, despite that being the seam
        // #2084 introduced. ResolveKindAsync reads the process-wide
        // DirectoryCache, which is NOT tenant-partitioned (see
        // DirectoryCache and the note in AgentEndpoints.AssignUnitAgentAsync).
        // This guard is the authoritative tenant-isolation seam precisely
        // because it reads only the tenant-filtered SpringDbContext in its
        // own scope — two entities visible to the same scope share a tenant
        // by construction. Routing it through the shared cache would
        // reintroduce the cross-tenant leak the guard exists to close.
        return await MemberVisibleAsync(member.Id, cancellationToken);
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

    private Task<bool> UnitVisibleAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.UnitDefinitions.AnyAsync(u => u.Id == unitId, cancellationToken);

    private Task<bool> AgentVisibleAsync(Guid agentId, CancellationToken cancellationToken) =>
        db.AgentDefinitions.AnyAsync(a => a.Id == agentId, cancellationToken);

    // Kind-agnostic membership visibility. An id is globally unique across
    // the two tables, so probing both answers "is this a tenant-visible
    // member candidate?" without trusting any claimed scheme. Agents are the
    // common member kind, so probe that table first to keep the typical add
    // at a single round-trip.
    private async Task<bool> MemberVisibleAsync(Guid memberId, CancellationToken cancellationToken) =>
        await AgentVisibleAsync(memberId, cancellationToken)
        || await UnitVisibleAsync(memberId, cancellationToken);
}
