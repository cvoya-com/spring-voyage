// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared binding-walk helper used by both
/// <see cref="ConnectorRuntimeContextResolver"/> (#2380) and
/// <see cref="ConnectorPromptContextResolver"/> (#2442). Walks the
/// subject's unit (and its ancestors via
/// <see cref="IUnitHierarchyResolver"/>) and returns the resolved
/// bindings — one per connector type id, with the closest unit's
/// binding winning over inherited bindings of the same type.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Cvoya.Spring.Dapr.Connectors</c> alongside the two
/// resolvers it serves. The runtime-context and prompt-context paths
/// were specified to share the same resolution semantics — extracting
/// the walk here keeps the two resolvers from drifting.
/// </para>
/// <para>
/// Registered as a singleton; the per-call membership lookup runs
/// through a fresh DI scope so the EF DbContext lifetime is respected.
/// </para>
/// </remarks>
public class ConnectorBindingWalker(
    IServiceScopeFactory scopeFactory,
    IUnitConnectorBindingStore bindingStore,
    IUnitHierarchyResolver hierarchyResolver,
    ILogger<ConnectorBindingWalker> logger)
{
    /// <summary>
    /// Maximum number of ancestor hops the walk will follow before
    /// bailing out. Matches the cap used by the runtime-context
    /// resolver before this helper existed.
    /// </summary>
    internal const int MaxDepth = 32;

    /// <summary>
    /// Resolves the bindings the resolver should hand to its
    /// contributors. Returns an empty list when the subject is not a
    /// unit / agent, when the agent has no membership, or when no
    /// unit in the walk carries a binding.
    /// </summary>
    /// <param name="subject">
    /// The dispatch target. Must be an <c>agent:</c> or <c>unit:</c>
    /// address — any other scheme is treated as a no-op.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the walk.</param>
    public async Task<IReadOnlyList<ResolvedConnectorBinding>> WalkAsync(
        Address subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!string.Equals(subject.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var startingUnitId = await ResolveStartingUnitAsync(subject, cancellationToken);
        if (startingUnitId is null)
        {
            return [];
        }

        return await CollectBindingsAsync(startingUnitId.Value, cancellationToken);
    }

    private async Task<Guid?> ResolveStartingUnitAsync(Address subject, CancellationToken cancellationToken)
    {
        if (string.Equals(subject.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return subject.Id;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var membershipRepo = scope.ServiceProvider.GetService<IUnitMembershipRepository>();
        if (membershipRepo is null)
        {
            // No membership repo available — fall back to treating the subject as a
            // unit-as-agent (ADR-0017) so the binding walk still runs.
            return subject.Id;
        }

        var memberships = await membershipRepo.ListByAgentAsync(subject.Id, cancellationToken);

        // Per ADR-0017 a unit IS an agent. Units live in unit_subunit_memberships,
        // not unit_memberships, so ListByAgentAsync returns empty for a unit-as-agent
        // subject. Fall back to the subject id itself so CollectBindingsAsync can find
        // the unit's own connector binding and walk its ancestors via IUnitHierarchyResolver.
        return memberships.Count == 0 ? subject.Id : memberships[0].UnitId;
    }

    private async Task<IReadOnlyList<ResolvedConnectorBinding>> CollectBindingsAsync(
        Guid startingUnitId,
        CancellationToken cancellationToken)
    {
        var byType = new Dictionary<Guid, ResolvedConnectorBinding>();
        var queue = new Queue<(Guid UnitId, int Depth)>();
        var visited = new HashSet<Guid>();
        queue.Enqueue((startingUnitId, 0));

        while (queue.Count > 0)
        {
            var (currentUnitId, depth) = queue.Dequeue();
            if (depth > MaxDepth)
            {
                logger.LogWarning(
                    "Connector binding walk exceeded max depth {MaxDepth} starting from unit {Unit:N}; bailing out.",
                    MaxDepth, startingUnitId);
                break;
            }

            if (!visited.Add(currentUnitId))
            {
                continue;
            }

            var binding = await bindingStore.GetAsync(currentUnitId, cancellationToken);
            if (binding is not null && !byType.ContainsKey(binding.TypeId))
            {
                byType[binding.TypeId] = new ResolvedConnectorBinding(currentUnitId, binding);
            }

            var parents = await hierarchyResolver.GetParentsAsync(
                new Address(Address.UnitScheme, currentUnitId), cancellationToken);
            foreach (var parent in parents)
            {
                queue.Enqueue((parent.Id, depth + 1));
            }
        }

        return [.. byType.Values];
    }
}

/// <summary>
/// One entry of a connector binding walk — the binding payload plus
/// the unit that owns it (direct on the subject's unit, or an
/// ancestor reached via <see cref="IUnitHierarchyResolver"/>).
/// </summary>
/// <param name="OwnerUnitId">
/// The unit the binding is persisted against. Equal to the subject's
/// unit when the binding is direct; the closest ancestor's id when it
/// is inherited.
/// </param>
/// <param name="Binding">The persisted binding payload.</param>
public sealed record ResolvedConnectorBinding(Guid OwnerUnitId, UnitConnectorBinding Binding);
