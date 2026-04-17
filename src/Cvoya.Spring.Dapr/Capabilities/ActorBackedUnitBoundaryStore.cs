// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitBoundaryStore"/>: persists boundary configuration
/// on the unit actor itself (same state-store pattern the actor uses for own
/// expertise, connector binding, and metadata). A unit with no configured
/// boundary returns <see cref="UnitBoundary.Empty"/>.
/// </summary>
/// <remarks>
/// <para>
/// Actor reads are looked up through the directory service to resolve
/// address path → actor id — same resolution rule the store and aggregator
/// use. A missing directory entry returns <see cref="UnitBoundary.Empty"/>
/// rather than throwing so the boundary decorator can safely treat unknown
/// units as "transparent" instead of failing the read.
/// </para>
/// <para>
/// Writes call <see cref="IUnitActor.SetBoundaryAsync"/>; reads call
/// <see cref="IUnitActor.GetBoundaryAsync"/>. Both invalidate the
/// aggregator cache implicitly because the actor is the only write path
/// and its callers invalidate through the HTTP / CLI endpoints.
/// </para>
/// </remarks>
public class ActorBackedUnitBoundaryStore(
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory) : IUnitBoundaryStore
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ActorBackedUnitBoundaryStore>();

    /// <inheritdoc />
    public async Task<UnitBoundary> GetAsync(Address unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (!string.Equals(unit.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            return UnitBoundary.Empty;
        }

        var entry = await SafeResolveAsync(unit, cancellationToken);
        if (entry is null)
        {
            return UnitBoundary.Empty;
        }

        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(entry.ActorId), nameof(UnitActor));
            var boundary = await proxy.GetBoundaryAsync(cancellationToken);
            return boundary ?? UnitBoundary.Empty;
        }
        catch (Exception ex)
        {
            // A transient actor read failure must not poison aggregation; log
            // and treat as "no boundary configured" so the decorator falls
            // back to the transparent view.
            _logger.LogWarning(ex,
                "Failed to read boundary for {Scheme}://{Path}; treating as empty.",
                unit.Scheme, unit.Path);
        }

        return UnitBoundary.Empty;
    }

    /// <inheritdoc />
    public async Task SetAsync(Address unit, UnitBoundary boundary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(boundary);

        if (!string.Equals(unit.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Boundary can only be persisted on a unit address (got scheme '{unit.Scheme}').",
                nameof(unit));
        }

        var entry = await SafeResolveAsync(unit, cancellationToken);
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"Cannot set boundary: unit '{unit.Path}' not found in directory.");
        }

        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));
        await proxy.SetBoundaryAsync(boundary, cancellationToken);
    }

    private async Task<DirectoryEntry?> SafeResolveAsync(Address address, CancellationToken ct)
    {
        try
        {
            return await directoryService.ResolveAsync(address, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Directory resolve failed for {Scheme}://{Path}; treating as unknown.",
                address.Scheme, address.Path);
            return null;
        }
    }
}