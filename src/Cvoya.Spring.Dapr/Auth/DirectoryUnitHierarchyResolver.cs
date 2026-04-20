// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitHierarchyResolver"/>. Finds the parent unit(s) of
/// a given child by scanning the directory and inspecting each unit's
/// member list — the same pattern used by
/// <see cref="Cvoya.Spring.Dapr.Capabilities.ExpertiseAggregator"/> for
/// ancestor walks during cache invalidation.
/// </summary>
/// <remarks>
/// <para>
/// The scan is O(units) today. For the current data volume — units are tens
/// to hundreds per deployment and the permission walk is a request-time
/// check that happens at most once per authorized endpoint call — that's
/// acceptable. When the directory grows a reverse-membership index this
/// resolver can be swapped out via DI without touching
/// <see cref="PermissionService"/>.
/// </para>
/// <para>
/// Failures to read member lists from individual unit actors (e.g. actor
/// unavailable) are logged and treated as "this unit is not a parent of the
/// child" — a permission check must never fail closed because one sibling
/// unit is down. Directory failures stop the walk and return the caller an
/// empty list, so the permission service degrades to "no inheritance"
/// rather than incorrectly promoting a human to admin.
/// </para>
/// </remarks>
public class DirectoryUnitHierarchyResolver(
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IUnitHierarchyResolver
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DirectoryUnitHierarchyResolver>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetParentsAsync(Address child, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!string.Equals(child.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            // Permission hierarchy walks only unit → unit links. An agent
            // address is not a member-of-unit candidate along the upstream
            // path the permission resolver cares about.
            return Array.Empty<Address>();
        }

        IReadOnlyList<DirectoryEntry> all;
        try
        {
            all = await directoryService.ListAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Directory ListAll failed while resolving parents of {Child}; returning empty.",
                child);
            return Array.Empty<Address>();
        }

        // #745: open one DI scope for the whole walk so the scoped
        // IUnitMembershipTenantGuard (and its SpringDbContext) is reused
        // across candidate checks. The resolver itself is a singleton,
        // so we can't take the guard as a constructor dependency.
        using var scope = scopeFactory.CreateScope();
        var tenantGuard = scope.ServiceProvider.GetRequiredService<IUnitMembershipTenantGuard>();

        var parents = new List<Address>();
        foreach (var entry in all)
        {
            if (!string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Address == child)
            {
                continue;
            }

            // #745: defensively filter cross-tenant candidates. The
            // DirectoryService cache is still shared across tenants at the
            // in-memory layer, so ListAllAsync can return units from other
            // tenants. The tenant guard consults the tenant-scoped entity
            // rows (which DO carry TenantId + query filter) to decide
            // whether the candidate is visible — a parent outside the
            // child's tenant is skipped before we ever read its actor
            // state, matching the issue #745 requirement to "not traverse
            // across tenants, even if data were inconsistent".
            bool shareTenant;
            try
            {
                shareTenant = await tenantGuard.ShareTenantAsync(
                    entry.Address, child, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Tenant guard threw while inspecting candidate parent {Parent} for child {Child}; skipping conservatively.",
                    entry.Address, child);
                continue;
            }
            if (!shareTenant)
            {
                continue;
            }

            Address[] members;
            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(entry.ActorId), nameof(UnitActor));
                members = await proxy.GetMembersAsync(cancellationToken) ?? Array.Empty<Address>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to read members of {Unit} while resolving parents of {Child}; skipping.",
                    entry.Address, child);
                continue;
            }

            if (Array.Exists(members, m => m == child))
            {
                parents.Add(entry.Address);
            }
        }

        return parents;
    }
}