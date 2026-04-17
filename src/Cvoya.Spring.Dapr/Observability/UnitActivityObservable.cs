// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Reactive.Linq;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitActivityObservable"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Walks the unit's member graph breadth-first at subscribe time, collecting
/// the set of <see cref="Address"/> values whose events should be surfaced.
/// The resulting set includes the unit itself, every direct agent member,
/// and every transitively nested sub-unit and its agent members (bounded by
/// <see cref="MaxWalkDepth"/> to guard against accidental cycles the
/// containment-cycle check missed).
/// </para>
/// <para>
/// The observable is a filtered view over the platform-wide
/// <see cref="IActivityEventBus"/> — no separate hot subject is created, so
/// ordering and back-pressure are consistent across the SSE relay, cost
/// aggregator, and persister. This is the
/// <c>Observable.Merge(unit.Members.Select(m =&gt; m.ActivityStream))</c>
/// shape from the Phase-2 observability design, realised as a filter because
/// every member already publishes to the same bus.
/// </para>
/// </remarks>
public sealed class UnitActivityObservable(
    IActivityEventBus activityEventBus,
    IActorProxyFactory actorProxyFactory,
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : IUnitActivityObservable
{
    /// <summary>
    /// Maximum number of unit levels walked when collecting the member set.
    /// </summary>
    internal const int MaxWalkDepth = 64;

    private readonly ILogger _logger = loggerFactory.CreateLogger<UnitActivityObservable>();

    /// <inheritdoc />
    public async Task<IObservable<ActivityEvent>> GetStreamAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        var visited = new HashSet<Address>(new AddressComparer());
        visited.Add(new Address("unit", unitId));

        await CollectMembersAsync(unitId, visited, depth: 0, cancellationToken);

        _logger.LogDebug(
            "UnitActivityObservable resolved {Count} member address(es) for unit {UnitId}.",
            visited.Count, unitId);

        // Snapshot the set so the filter closure doesn't hold a live reference
        // to a mutable collection. The observable is hot — subscribers see
        // events published after they subscribe — and the filter predicate
        // below runs on the producer thread for every event on the bus.
        var frozen = visited.ToHashSet(new AddressComparer());
        return activityEventBus.ActivityStream.Where(evt => frozen.Contains(evt.Source));
    }

    private async Task CollectMembersAsync(
        string unitId, HashSet<Address> visited, int depth, CancellationToken ct)
    {
        if (depth > MaxWalkDepth)
        {
            _logger.LogWarning(
                "UnitActivityObservable stopping walk at depth {Depth} for unit {UnitId}; suspected cycle.",
                depth, unitId);
            return;
        }

        Address[] members;
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(unitId), nameof(UnitActor));
            members = await proxy.GetMembersAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "UnitActivityObservable failed to read members of unit {UnitId}; treating as empty.",
                unitId);
            return;
        }

        foreach (var member in members)
        {
            if (!visited.Add(member))
            {
                continue;
            }

            if (string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                var subUnitActorId = await ResolveSubUnitActorIdAsync(member, ct);
                if (subUnitActorId is not null)
                {
                    await CollectMembersAsync(subUnitActorId, visited, depth + 1, ct);
                }
            }
        }
    }

    private async Task<string?> ResolveSubUnitActorIdAsync(Address member, CancellationToken ct)
    {
        try
        {
            var entry = await directoryService.ResolveAsync(member, ct);
            return entry?.ActorId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "UnitActivityObservable failed to resolve sub-unit {Member}; skipping.",
                member);
            return null;
        }
    }

    /// <summary>
    /// Compares <see cref="Address"/> values by scheme + path (case-insensitive
    /// scheme) so the de-duplication is reliable even across slightly
    /// different spellings.
    /// </summary>
    private sealed class AddressComparer : IEqualityComparer<Address>
    {
        public bool Equals(Address? x, Address? y)
        {
            if (x is null || y is null)
            {
                return ReferenceEquals(x, y);
            }

            return string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Path, y.Path, StringComparison.Ordinal);
        }

        public int GetHashCode(Address obj) =>
            HashCode.Combine(
                obj.Scheme is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Scheme),
                obj.Path is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Path));
    }
}