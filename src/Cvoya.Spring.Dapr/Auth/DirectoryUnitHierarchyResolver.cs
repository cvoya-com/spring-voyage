// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitHierarchyResolver"/>. Reads parent edges
/// directly from <c>unit_subunit_memberships</c> via
/// <see cref="IUnitSubunitMembershipRepository"/>. Tenant-root edges
/// (<c>parent_id == tenant.id</c>) are filtered out — the permission
/// resolver walks unit → unit links only and treats top-level units as
/// terminal roots.
/// </summary>
/// <remarks>
/// <para>
/// Pre-#2052 this resolver scanned the directory and consulted each unit
/// actor's <c>GetMembersAsync</c> by proxy. With <c>unit_subunit_memberships</c>
/// authoritative, an indexed SQL lookup is the simpler shape — one query
/// per child instead of <c>O(units)</c> proxy calls. The cloud overlay can
/// still swap in a tenant-aware decorator via DI.
/// </para>
/// <para>
/// The resolver runs as a singleton (consumers are themselves singleton
/// or scoped, but they need a single shared instance behind their per-
/// request <c>SpringDbContext</c>). It opens a fresh DI scope per call so
/// the scoped repository (and the <c>SpringDbContext</c> behind it)
/// resolve cleanly.
/// </para>
/// </remarks>
public class DirectoryUnitHierarchyResolver(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IUnitHierarchyResolver
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DirectoryUnitHierarchyResolver>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetParentsAsync(Address child, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!string.Equals(child.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            // The permission hierarchy walks only unit → unit links. An
            // agent address is not a member-of-unit candidate along the
            // upstream path the permission resolver cares about.
            return Array.Empty<Address>();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var tenantId = tenantContext.CurrentTenantId;

        IReadOnlyList<UnitSubunitMembership> rows;
        try
        {
            rows = await repo.ListByChildAsync(child.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Hierarchy resolver: failed to read parent edges for {Child}; returning empty.",
                child);
            return Array.Empty<Address>();
        }

        if (rows.Count == 0)
        {
            return Array.Empty<Address>();
        }

        // #2052: the explicit tenant-root edge (parent_id == tenant.id)
        // is the top-level marker. The permission resolver walks unit →
        // unit links, so we must NOT surface tenant-root edges as
        // parents — top-level units are terminal in the inheritance
        // walk; their inherited grants flow from the tenant fall-
        // through, not from a unit-shaped ancestor.
        var parents = new List<Address>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ParentId == tenantId)
            {
                continue;
            }

            parents.Add(new Address(Address.UnitScheme, row.ParentId));
        }

        return parents;
    }
}
