// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExpertiseStore"/>: reads per-agent and per-unit
/// <em>own</em> expertise directly from the EF-backed coordinators
/// (<see cref="IAgentStateCoordinator"/>, <see cref="IUnitStateCoordinator"/>),
/// which front <c>spring.agent_live_config</c> / <c>spring.unit_expertise</c>.
/// Address scheme dispatches the read; an unknown scheme returns an empty
/// list instead of throwing so the aggregator can safely walk
/// heterogeneous member graphs.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2081: this store used to read expertise via Dapr actor proxies
/// (<c>IAgentActor.GetExpertiseAsync</c> / <c>IUnitActor.GetOwnExpertiseAsync</c>).
/// When the aggregator was driven from inside an actor turn (e.g. during
/// the <c>UnitActor</c> dispatch pipeline), the proxy call re-entered the
/// same actor and deadlocked on Dapr's turn-based concurrency lock until
/// <c>HttpClient.Timeout</c> fired. The EF-backed coordinators are
/// singletons with internal scoped <c>SpringDbContext</c> scopes, so they
/// can be injected into this singleton store and read without any actor
/// round-trip. The actor methods (<c>GetExpertiseAsync</c> /
/// <c>GetOwnExpertiseAsync</c>) themselves go through the same
/// coordinators, so the data source is unchanged — we simply skip the
/// actor hop.
/// </para>
/// <para>
/// The directory resolve stays in place: it owns the address-path-to-
/// actor-id resolution rule the routing layer uses, and the entity-id
/// it returns is the EF row key the coordinators consume.
/// </para>
/// </remarks>
public class ActorBackedExpertiseStore(
    IDirectoryService directoryService,
    IAgentStateCoordinator agentStateCoordinator,
    IUnitStateCoordinator unitStateCoordinator,
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

        var entityIdString = GuidFormatter.Format(entry.ActorId);

        try
        {
            if (string.Equals(entity.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
            {
                var domains = await agentStateCoordinator.GetExpertiseAsync(
                    entityIdString, cancellationToken);
                return domains ?? Array.Empty<ExpertiseDomain>();
            }

            if (string.Equals(entity.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
            {
                var domains = await unitStateCoordinator.GetOwnExpertiseAsync(
                    entityIdString, cancellationToken);
                return domains ?? Array.Empty<ExpertiseDomain>();
            }
        }
        catch (Exception ex)
        {
            // A transient EF read failure must not poison aggregation; log
            // and treat as "no expertise from this contributor" so the caller
            // still gets the rest of the tree.
            _logger.LogWarning(ex,
                "Failed to read expertise for {Address}; treating as empty.",
                entity);
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
                "Directory resolve failed for {Address}; treating as unknown.",
                address);
            return null;
        }
    }
}
