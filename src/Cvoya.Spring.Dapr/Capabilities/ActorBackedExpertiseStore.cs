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
/// Default <see cref="IExpertiseStore"/>: reads per-agent expertise from the
/// agent actor via <see cref="IAgentActor.GetExpertiseAsync"/>, and per-unit
/// <em>own</em> expertise from the unit actor via
/// <see cref="IUnitActor.GetOwnExpertiseAsync"/>. Address scheme dispatches
/// the read; an unknown scheme returns an empty list instead of throwing so
/// the aggregator can safely walk heterogeneous member graphs.
/// </summary>
/// <remarks>
/// Actor reads are looked up through the directory service to resolve
/// address path → actor id — this matches the resolution rule the routing
/// layer uses and keeps the store agnostic of how an address was spelled on
/// the wire. A missing directory entry or a transient actor error returns
/// an empty list and logs a warning; the aggregator then treats that
/// contributor as "no expertise" rather than failing the whole read.
/// </remarks>
public class ActorBackedExpertiseStore(
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory) : IExpertiseStore
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ActorBackedExpertiseStore>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseDomain>> GetDomainsAsync(
        Address entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entry = await SafeResolveAsync(entity, cancellationToken);
        if (entry is null)
        {
            return Array.Empty<ExpertiseDomain>();
        }

        try
        {
            if (string.Equals(entity.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            {
                var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                    new ActorId(entry.ActorId), nameof(AgentActor));
                var domains = await proxy.GetExpertiseAsync(cancellationToken);
                return domains ?? Array.Empty<ExpertiseDomain>();
            }

            if (string.Equals(entity.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(entry.ActorId), nameof(UnitActor));
                var domains = await proxy.GetOwnExpertiseAsync(cancellationToken);
                return domains ?? Array.Empty<ExpertiseDomain>();
            }
        }
        catch (Exception ex)
        {
            // A transient actor read failure must not poison aggregation; log
            // and treat as "no expertise from this contributor" so the caller
            // still gets the rest of the tree.
            _logger.LogWarning(ex,
                "Failed to read expertise for {Scheme}://{Path}; treating as empty.",
                entity.Scheme, entity.Path);
        }

        return Array.Empty<ExpertiseDomain>();
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