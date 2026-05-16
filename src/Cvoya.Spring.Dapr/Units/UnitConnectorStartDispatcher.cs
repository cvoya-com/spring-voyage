// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitConnectorStartDispatcher"/> implementation (#2156).
/// Reads the unit's binding from the EF-backed
/// <see cref="IUnitConnectorConfigStore"/> and routes to the matching
/// <see cref="IConnectorType"/> instance, mirroring the body of the legacy
/// private static <c>DispatchConnectorStartAsync</c> on <c>UnitEndpoints</c>.
/// </summary>
/// <remarks>
/// #2359: lives in the shared Dapr module so the Worker host (where
/// <see cref="Actors.UnitActor"/> runs and calls this from its post-validation
/// auto-start hook) resolves the same implementation the API host uses. Prior
/// to #2359 this lived in <c>Cvoya.Spring.Host.Api</c> and was only registered
/// in the API host's DI container — every actor-driven auto-start saw a null
/// dispatcher and silently aborted the <c>Stopped → Starting → Running</c>
/// sequence, leaving units stuck in <c>Stopped</c>.
/// </remarks>
public class UnitConnectorStartDispatcher : IUnitConnectorStartDispatcher
{
    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IReadOnlyList<IConnectorType> _connectorTypes;
    private readonly ILogger<UnitConnectorStartDispatcher> _logger;

    /// <summary>
    /// Initialises a new <see cref="UnitConnectorStartDispatcher"/>.
    /// </summary>
    public UnitConnectorStartDispatcher(
        IUnitConnectorConfigStore configStore,
        IEnumerable<IConnectorType> connectorTypes,
        ILogger<UnitConnectorStartDispatcher> logger)
    {
        _configStore = configStore;
        _connectorTypes = connectorTypes.ToList();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DispatchAsync(string unitActorId, CancellationToken cancellationToken = default)
    {
        var binding = await _configStore.GetAsync(unitActorId, cancellationToken);
        if (binding is null)
        {
            return;
        }

        var connector = _connectorTypes.FirstOrDefault(c => c.TypeId == binding.TypeId);
        if (connector is null)
        {
            _logger.LogWarning(
                "Unit {UnitId} is bound to connector type {TypeId} which is not registered; skipping start hook.",
                unitActorId, binding.TypeId);
            return;
        }

        try
        {
            await connector.OnUnitStartingAsync(unitActorId, cancellationToken);
        }
        catch (System.Exception ex)
        {
            // Any connector start failure is non-fatal — the unit
            // transitions to Running regardless so the container stays up.
            _logger.LogError(ex,
                "Connector {Slug} start hook threw for unit {UnitId}; continuing unit start.",
                connector.Slug, unitActorId);
        }
    }
}
