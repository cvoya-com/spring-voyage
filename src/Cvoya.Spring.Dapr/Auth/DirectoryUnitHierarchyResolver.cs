// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

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