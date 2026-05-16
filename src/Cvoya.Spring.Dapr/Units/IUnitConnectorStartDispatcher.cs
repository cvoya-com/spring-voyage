// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

/// <summary>
/// Dispatcher seam for the unit-start connector hook (#2156). Lifted out of
/// the API endpoint so the actor's auto-start path (post-validation) can run
/// the same logic the <c>POST /units/{id}/start</c> endpoint runs, without
/// duplicating the EF-store / <see cref="Cvoya.Spring.Connectors.IConnectorType"/>
/// lookup. Both the interface and the default <see cref="UnitConnectorStartDispatcher"/>
/// implementation live in <c>Cvoya.Spring.Dapr</c> so the Worker host (where
/// <see cref="Actors.UnitActor"/> runs and calls this dispatcher) resolves a
/// real implementation — see #2359 for the production bug where this lived in
/// the API host and the Worker silently saw a null dispatcher.
/// </summary>
public interface IUnitConnectorStartDispatcher
{
    /// <summary>
    /// Resolves the unit's persisted connector binding (if any), looks up
    /// the matching connector type, and invokes its <c>OnUnitStartingAsync</c>
    /// hook. Connector failures are logged and swallowed; this method never
    /// throws on the connector's own faults so a misbehaving connector
    /// cannot block a unit transitioning to <c>Running</c>.
    /// </summary>
    /// <param name="unitActorId">
    /// The unit's canonical actor-id string (32-char no-dash hex Guid).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task DispatchAsync(string unitActorId, CancellationToken cancellationToken = default);
}
